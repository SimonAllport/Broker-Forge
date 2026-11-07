#!/usr/bin/env bash
# Deploy the BrokerForge dev environment (infra/main.bicep) and wait for the host to be healthy.
# Reads the app registration and environment IDs from $BF_HOME/spike-ids.env (phase 0; BF_HOME defaults to ~/.brokerforge)
# and the phase 3 connect app IDs (tools/dev/setup-connect-app.sh) from $BF_HOME/dev-ids.env, and writes the deployed
# names back to dev-ids.env, keeping the IDs it does not own. Generated secrets go straight into Key Vault and are
# never printed or written to the repo.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
IDS="${BF_HOME:-$HOME/.brokerforge}/spike-ids.env"
OUT="${BF_HOME:-$HOME/.brokerforge}/dev-ids.env"
RG="${BF_RG:-rg-brokerforge-dev}"
LOCATION="${BF_LOCATION:-switzerlandnorth}"
# BF_MOCKS=false leaves out the sample APIs (tools/install/install.sh without --with-samples).
MOCKS="${BF_MOCKS:-true}"
# The Azure resources' name prefix (infra/main.bicep); an install sets its own.
PREFIX="${BF_PREFIX:-bf-dev}"
# shellcheck disable=SC1090
source "$IDS"
: "${HOST_APP:?} ${PLUGIN_APP:?} ${TENANT:?} ${ENVID:?}"
# Phase 3 IDs from setup-connect-app.sh; empty until it has run, which leaves the per-user sources out.
devid() { [ -f "$OUT" ] && sed -n "s/^$1=//p" "$OUT" | tail -1 || true; }
CONNECT_APP="$(devid CONNECT_APP)" TICKETS_API_APP="$(devid TICKETS_API_APP)"
TICKETS_ALICE_OID="$(devid TICKETS_ALICE_OID)" TICKETS_BOB_OID="$(devid TICKETS_BOB_OID)"
# A second environment (plan 5), when ~/.brokerforge/dev-ids.env names one: SECOND_ENVID, SECOND_ORGURL, SECOND_PLUGIN_APP.
SECOND_ENVID="$(devid SECOND_ENVID)" SECOND_ORGURL="$(devid SECOND_ORGURL)" SECOND_PLUGIN_APP="$(devid SECOND_PLUGIN_APP)"
# The dev environment's org URL (DV_ORG, read by dv.sh from ~/.brokerforge/spike-ids.env): the host reads its connection designs there.
# shellcheck disable=SC1091
source "$ROOT/tools/spike/dv.sh"

az bicep version >/dev/null 2>&1 || az bicep install
for ns in Microsoft.App Microsoft.ContainerRegistry Microsoft.KeyVault Microsoft.Storage Microsoft.OperationalInsights Microsoft.Insights Microsoft.ManagedIdentity; do
  az provider register --namespace "$ns" --wait -o none
done

echo "== resource group $RG ($LOCATION)"
az group create -n "$RG" -l "$LOCATION" -o none
DEPLOYER="$(az ad signed-in-user show --query id -o tsv)"

# A deployment that fails is tried twice more, 30 seconds apart: on a new resource group Azure can fail to find a
# resource it has just created (a new Key Vault, in the install test). Every resource is declared, so a re-run is safe.
# After the last try it prints Azure's messages: from the failed operations when the deployment ran, from validation
# when it didn't (az itself prints only "Inner Errors:" then).
messages() { sed -n 's/.*"message": *"\([^"]*\)".*/   \1/p' | grep -v "Please list deployment operations" | sort -u >&2; }
deploy() { # deploy <name> <output query> <parameters...>
  local name="$1" query="$2" attempt; shift 2
  for attempt in 1 2 3; do
    az deployment group create -g "$RG" -n "$name" -f "$ROOT/infra/main.bicep" -p "$@" --query "$query" -o tsv && return 0
    [ "$attempt" = 3 ] && break
    echo "   deployment $name failed; trying again in 30 seconds" >&2
    sleep 30
  done
  echo "deployment $name failed; Azure says:" >&2
  { az deployment operation group list -g "$RG" -n "$name" -o json 2>/dev/null
    az deployment group validate -g "$RG" -f "$ROOT/infra/main.bicep" -p "$@" -o json 2>&1; } | messages
  return 1
}
trap 'rm -f "${secret_file:-}"' EXIT

echo "== base: identity, registry, Key Vault, monitoring"
base="$(deploy bf-base "[properties.outputs.registryName.value, properties.outputs.vaultName.value]" \
  deployApps=false deployerObjectId="$DEPLOYER" prefix="$PREFIX")"
ACR="$(sed -n 1p <<< "$base")"
VAULT="$(sed -n 2p <<< "$base")"

echo "== secrets in $VAULT (generated once, never printed)"
secret_file="$(mktemp)"
names="page-token-key"
[ "$MOCKS" = true ] && names="$names orders-api-key inventory-api-key tickets-client-secret tickets-alice-password tickets-bob-password"
for name in $names; do
  if az keyvault secret show --vault-name "$VAULT" -n "$name" --query id -o tsv >/dev/null 2>&1; then
    echo "   $name exists"
    continue
  fi
  (umask 077; openssl rand -hex 32 | tr -d '\n' > "$secret_file")
  # Role assignments can take a minute to reach Key Vault.
  for attempt in 1 2 3 4 5 6 7 8 9 10; do
    if az keyvault secret set --vault-name "$VAULT" -n "$name" --file "$secret_file" -o none 2>/dev/null; then
      echo "   $name created"
      break
    fi
    [ "$attempt" = 10 ] && { echo "could not write $name to $VAULT" >&2; exit 1; }
    sleep 15
  done
done

# A dirty tree gets a tag from its diff: reusing one "-dirty" tag would leave the old revision running.
TAG="$(git -C "$ROOT" rev-parse --short HEAD)$(git -C "$ROOT" diff --quiet || echo "-dirty-$(git -C "$ROOT" diff | shasum | cut -c1-8)")"
echo "== images $TAG"
# A tag already in the registry was built from the same tree, so a second run (the installer's) skips the build.
built() { az acr repository show -n "$ACR" --image "$1" -o none >/dev/null 2>&1; }
built "brokerforge-host:$TAG" || az acr build -r "$ACR" -t "brokerforge-host:$TAG" -f "$ROOT/src/BrokerForge.Host/Dockerfile" "$ROOT" --no-logs -o none
if [ "$MOCKS" = true ] && ! built "brokerforge-orders-mock:$TAG"; then
  az acr build -r "$ACR" -t "brokerforge-orders-mock:$TAG" "$ROOT/tools/mock-api" --no-logs -o none
fi
SERVER="$(az acr show -n "$ACR" --query loginServer -o tsv)"
MOCK_IMAGE=""
[ "$MOCKS" = true ] && MOCK_IMAGE="$SERVER/brokerforge-orders-mock:$TAG"

echo "== apps"
HOST_URL="$(deploy bf-apps properties.outputs.hostUrl.value \
  deployApps=true deployerObjectId="$DEPLOYER" prefix="$PREFIX" \
     hostImage="$SERVER/brokerforge-host:$TAG" mockImage="$MOCK_IMAGE" deployMocks="$MOCKS" \
     tenantId="$TENANT" hostAudience="$HOST_APP" pluginClientId="$PLUGIN_APP" environmentId="$ENVID" orgUrl="$DV_ORG" \
     connectClientId="$CONNECT_APP" ticketsApiAppId="$TICKETS_API_APP" ticketsAliceOid="$TICKETS_ALICE_OID" ticketsBobOid="$TICKETS_BOB_OID" \
     secondEnvironmentId="$SECOND_ENVID" secondOrgUrl="$SECOND_ORGURL" secondPluginClientId="$SECOND_PLUGIN_APP")"

keep="$(mktemp)"
[ -f "$OUT" ] && grep -Ev '^(RG|ACR|VAULT|DEV_HOST_URL|IMAGE_TAG|PREFIX)=' "$OUT" > "$keep" || true
{ printf 'RG=%s\nACR=%s\nVAULT=%s\nDEV_HOST_URL=%s\nIMAGE_TAG=%s\nPREFIX=%s\n' "$RG" "$ACR" "$VAULT" "$HOST_URL" "$TAG" "$PREFIX"; cat "$keep"; } > "$OUT"
rm -f "$keep"

echo "== waiting for $HOST_URL/healthz, and for the host and mocks to run image $TAG alone"
# Healthy is not enough: the outgoing revision keeps answering until it is deactivated (phase 1 findings 5 and 8,
# phase 2 finding 6 for the mock).
active() { az containerapp revision list -g "$RG" -n "$1" --query "[?properties.active].properties.template.containers[0].image" -o tsv | sort -u; }
for attempt in $(seq 1 30); do
  code="$(curl -s -o /dev/null -w '%{http_code}' "$HOST_URL/healthz" || true)"
  if [ "$code" = 200 ] && [ "$(active "$PREFIX-host")" = "$SERVER/brokerforge-host:$TAG" ] \
     && { [ "$MOCKS" != true ] || [ "$(active "$PREFIX-orders-mock")" = "$MOCK_IMAGE" ]; } \
     && { [ "$MOCKS" != true ] || [ -z "$CONNECT_APP" ] || [ "$(active "$PREFIX-tickets-mock")" = "$MOCK_IMAGE" ]; }; then
    echo "healthy: $HOST_URL (ids in $OUT)"
    exit 0
  fi
  sleep 10
done
echo "host did not become healthy; check: az containerapp logs show -g $RG -n $PREFIX-host --tail 100" >&2
exit 1
