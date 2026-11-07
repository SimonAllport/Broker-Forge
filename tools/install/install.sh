#!/usr/bin/env bash
# Install BrokerForge into one Dataverse environment and one Azure resource group (docs/install.md).
# Usage: tools/install/install.sh <settings file>   (see tools/install/brokerforge.env.example)
#
# Safe to run again: every step looks for what it makes first. It runs as the signed-in az CLI user, who needs to
# create app registrations in Entra, Contributor and User Access Administrator (or Owner) on the subscription, and
# System Administrator in the Dataverse environment. No secret is printed or written to a file.
#
# The IDs it makes are kept in $BF_HOME (default ~/.brokerforge/installs/<resource group>), outside the repo, in the
# two files the dev scripts also read: spike-ids.env (app registrations, environment) and dev-ids.env (Azure names).
set -euo pipefail

SETTINGS="${1:?settings file, e.g. brokerforge.env (from tools/install/brokerforge.env.example)}"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PY="${PYTHON:-$( [ -x /usr/bin/python3 ] && echo /usr/bin/python3 || echo python3 )}"
# shellcheck disable=SC1090
source "$SETTINGS"
: "${ORG_URL:?ORG_URL in $SETTINGS} ${ENVIRONMENT_ID:?ENVIRONMENT_ID in $SETTINGS}"
ORG_URL="${ORG_URL%/}"
RESOURCE_GROUP="${RESOURCE_GROUP:-rg-brokerforge}"
: "${LOCATION:?LOCATION in $SETTINGS: an Azure region open to your subscription, such as uksouth}"
PREFIX="${PREFIX:-brokerforge}"
SAMPLES="${SAMPLES:-false}"
PACKAGE="${PACKAGE:-BrokerForgeCore_managed.zip}"
case "$PACKAGE" in /*) ;; *) PACKAGE="$(cd "$(dirname "$SETTINGS")" && pwd)/$PACKAGE" ;; esac
[ -f "$PACKAGE" ] || { echo "the managed core solution $PACKAGE is missing: download it from the GitHub release" >&2; exit 1; }
export BF_HOME="${BF_HOME:-$HOME/.brokerforge/installs/$RESOURCE_GROUP}"
mkdir -p "$BF_HOME"
IDS="$BF_HOME/spike-ids.env"
OUT="$BF_HOME/dev-ids.env"
touch "$IDS" "$OUT"

step() { echo "== $*"; }
setvar() { # setvar <file> <key> <value>
  local tmp; tmp="$(mktemp)"
  grep -v "^$2=" "$1" > "$tmp" || true
  echo "$2=$3" >> "$tmp"
  mv "$tmp" "$1"
}
getvar() { sed -n "s/^$2=//p" "$1" | tail -1; }
object_id() { az ad app show --id "$1" --query id -o tsv; }
graph_patch() { az rest --method PATCH --url "https://graph.microsoft.com/v1.0/applications/$(object_id "$1")" --headers Content-Type=application/json --body "$2" -o none; }
ensure_sp() { az ad sp show --id "$1" --query id -o tsv >/dev/null 2>&1 || az ad sp create --id "$1" -o none; }
# The app with this display name that this install made earlier, else a new one.
ensure_app() { # ensure_app <key> <display name>
  local app; app="$(getvar "$IDS" "$1")"
  [ -n "$app" ] || app="$(az ad app create --display-name "$2" --sign-in-audience AzureADMyOrg --query appId -o tsv)"
  setvar "$IDS" "$1" "$app"
  ensure_sp "$app"
  echo "$app"
}

for tool in az curl openssl shasum; do command -v "$tool" >/dev/null || { echo "$tool is needed" >&2; exit 1; }; done
"$PY" -c 'import sys; sys.exit(sys.version_info < (3, 8))' || { echo "python 3.8 or later is needed" >&2; exit 1; }
TENANT="$(az account show --query tenantId -o tsv)" || { echo "sign in first: az login" >&2; exit 1; }
setvar "$IDS" TENANT "$TENANT"
setvar "$IDS" ENVID "$ENVIRONMENT_ID"
setvar "$IDS" DV_ORG "$ORG_URL"
echo "installing into $ORG_URL and resource group $RESOURCE_GROUP (tenant $TENANT); IDs kept in $BF_HOME"

step "Entra: the host's app registration (the audience of the plug-in's token)"
HOST_APP="$(ensure_app HOST_APP "BrokerForge host ($RESOURCE_GROUP)")"
graph_patch "$HOST_APP" "{\"identifierUris\":[\"api://$HOST_APP\"],\"api\":{\"requestedAccessTokenVersion\":2}}"

step "Entra: the plug-in's app registration for this environment"
PLUGIN_APP="$(ensure_app PLUGIN_APP "BrokerForge plug-in ($RESOURCE_GROUP)")"
# Power Platform managed identity: the subject names the tenant, the environment and
# the SHA-256 of the certificate the released plug-in is signed with.
TENANT_B64="$("$PY" -c "import base64,uuid,sys; print(base64.urlsafe_b64encode(uuid.UUID(sys.argv[1]).bytes_le).decode().rstrip('='))" "$TENANT")"
HASH="$(shasum -a 256 "$ROOT/tools/install/plugin-signing.cer" | cut -d' ' -f1)"
SUBJECT="/eid1/c/pub/t/$TENANT_B64/a/qzXoWDkuqUa3l6zM5mM0Rw/n/plugin/e/$ENVIRONMENT_ID/h/$HASH"
if [ -z "$(az ad app federated-credential list --id "$PLUGIN_APP" --query "[?subject=='$SUBJECT'].name" -o tsv)" ]; then
  az ad app federated-credential create --id "$PLUGIN_APP" -o none --parameters \
    "{\"name\":\"dataverse-plugin\",\"issuer\":\"https://login.microsoftonline.com/$TENANT/v2.0\",\"subject\":\"$SUBJECT\",\"audiences\":[\"api://AzureADTokenExchange\"]}"
fi

step "Azure: first deploy (identity, registry, Key Vault, storage, monitoring, host)"
BF_RG="$RESOURCE_GROUP" BF_LOCATION="$LOCATION" BF_PREFIX="$PREFIX" BF_MOCKS="$SAMPLES" "$ROOT/tools/dev/deploy.sh"
HOST_URL="$(getvar "$OUT" DEV_HOST_URL)"

step "Entra: the connect app (users connect per-user sources with it; administrators sign in to Dataverse with it)"
CONNECT_APP="$(ensure_app CONNECT_APP "BrokerForge connect ($RESOURCE_GROUP)")"
setvar "$OUT" CONNECT_APP "$CONNECT_APP"
GRAPH=00000003-0000-0000-c000-000000000000
DYNAMICS=00000007-0000-0000-c000-000000000000
DYNAMICS_USER_IMPERSONATION=78ce3f0f-a1ce-49c2-8cde-64b5c0896db4
# Graph openid, profile and offline_access, and Dynamics CRM user_impersonation for the administrator's step (HLD D-38).
graph_patch "$CONNECT_APP" "{\"web\":{\"redirectUris\":[\"$HOST_URL/signin-oidc\",\"$HOST_URL/connect/callback\",\"$HOST_URL/admin/callback\"]},\"requiredResourceAccess\":[{\"resourceAppId\":\"$GRAPH\",\"resourceAccess\":[{\"id\":\"37f7f235-527c-4136-accd-4a02d197296e\",\"type\":\"Scope\"},{\"id\":\"14dad69e-099b-42c9-810b-d002981feec1\",\"type\":\"Scope\"},{\"id\":\"7427e0e9-2fba-42fe-b0c0-848c9e6a8182\",\"type\":\"Scope\"}]},{\"resourceAppId\":\"$DYNAMICS\",\"resourceAccess\":[{\"id\":\"$DYNAMICS_USER_IMPERSONATION\",\"type\":\"Scope\"}]}]}"
# The connect app has no secret: the host's managed identity is its federated credential.
MI_PRINCIPAL="$(az identity show -g "$RESOURCE_GROUP" -n "$PREFIX-id" --query principalId -o tsv)"
if [ -z "$(az ad app federated-credential list --id "$CONNECT_APP" --query "[?name=='bf-host-mi'].id | [0]" -o tsv)" ]; then
  az ad app federated-credential create --id "$CONNECT_APP" -o none --parameters \
    "{\"name\":\"bf-host-mi\",\"issuer\":\"https://login.microsoftonline.com/$TENANT/v2.0\",\"subject\":\"$MI_PRINCIPAL\",\"audiences\":[\"api://AzureADTokenExchange\"]}"
fi
az ad app permission admin-consent --id "$CONNECT_APP" -o none 2>/dev/null && echo "   admin consent granted" \
  || echo "   admin consent not granted: each user consents at first sign-in"

step "Azure: second deploy (the host with the connect app)"
BF_RG="$RESOURCE_GROUP" BF_LOCATION="$LOCATION" BF_PREFIX="$PREFIX" BF_MOCKS="$SAMPLES" "$ROOT/tools/dev/deploy.sh"

step "Dataverse: the managed core, its plug-in identity, host variables and the host's application user"
(cd "$ROOT/tools/dev" && DV_ORG="$ORG_URL" PLUGIN_APP="$PLUGIN_APP" "$PY" install_core.py "$PACKAGE")

step "Dataverse: BrokerForge administrator for you"
(cd "$ROOT/tools/dev" && DV_ORG="$ORG_URL" "$PY" - "$(az ad signed-in-user show --query id -o tsv)" <<'PY'
import sys
from setup_dataverse import API, call, first
oid = sys.argv[1]
user = first(f"systemusers?$select=systemuserid&$filter=azureactivedirectoryobjectid eq {oid}")
bu = first("businessunits?$select=businessunitid&$filter=_parentbusinessunitid_value eq null")["businessunitid"]
role = first(f"roles?$select=roleid&$filter=name eq 'BrokerForge administrator' and _businessunitid_value eq {bu}")
if not user or not role:
    sys.exit("you are not a user of this environment, or the core did not install the BrokerForge administrator role")
if not first(f"systemusers({user['systemuserid']})/systemuserroles_association?$select=roleid&$filter=roleid eq {role['roleid']}"):
    call("POST", f"systemusers({user['systemuserid']})/systemuserroles_association/$ref", {"@odata.id": f"{API}roles({role['roleid']})"})
print("   BrokerForge administrator assigned")
PY
)

cat <<DONE

BrokerForge is installed.
  Host:         $HOST_URL
  Environment:  $ORG_URL (open the BrokerForge app)
  IDs:          $BF_HOME
Next: docs/install.md, "After the install".
DONE
if [ "$SAMPLES" = true ]; then
  cat <<SAMPLE
Sample API base URL: $(az deployment group show -g "$RESOURCE_GROUP" -n bf-apps --query properties.outputs.mockUrl.value -o tsv)
Its key is the Key Vault secret inventory-api-key in $(getvar "$OUT" VAULT); docs/install.md says how to set it.
SAMPLE
fi
