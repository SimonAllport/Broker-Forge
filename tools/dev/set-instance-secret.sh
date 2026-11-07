#!/usr/bin/env bash
# Set one instance secret of a connection design (HLD D-34) in the dev host's store. Stopgap until plan 4's page.
# Usage: [BF_ENVIRONMENT=<environment id>] tools/dev/set-instance-secret.sh <Instance> <name> [<Key Vault secret name>]
#   e.g. tools/dev/set-instance-secret.sh WarehouseAPI apiKey inventory-api-key
# The value never leaves Azure and is never printed: the host's admin command runs inside the running host container
# (az containerapp exec), so it uses the host's own identity, table and Data Protection key ring, and it reads the
# value itself from the Key Vault reference. Only the reference is on the command line. The command refuses an
# instance and name that no connection design in that environment lists in its secretNames.
# Note: `--from` takes any kv: or env: reference, so whoever can exec into the host can copy any Key Vault secret or
# environment variable the host identity can read into an instance secret (and then have the host send it to a design's
# source). Exec rights on the host are therefore equivalent to reading its secrets; grant them only to people who may.
set -euo pipefail
INSTANCE="${1:?service instance}"
NAME="${2:?secret name}"
KV_SECRET="${3:-$(printf '%s' "$INSTANCE" | tr '[:upper:]' '[:lower:]')-$(printf '%s' "$NAME" | tr '[:upper:]' '[:lower:]')}"
# shellcheck disable=SC1090
source "${BF_HOME:-$HOME/.brokerforge}/spike-ids.env"
# shellcheck disable=SC1090
source "${BF_HOME:-$HOME/.brokerforge}/dev-ids.env"
# BF_ENVIRONMENT sets the key for another environment the host serves (plan 5: the second environment).
ENVID="${BF_ENVIRONMENT:-$ENVID}"
: "${ENVID:?} ${RG:?} ${VAULT:?}"
VAULT_URI="$(az keyvault show -n "$VAULT" --query properties.vaultUri -o tsv)"
# Fail here, by name, rather than in the container: the secret must exist (its value is not fetched).
[ -n "$(az keyvault secret list --vault-name "$VAULT" --query "[?name=='$KV_SECRET'].id" -o tsv)" ] \
  || { echo "Key Vault $VAULT has no secret $KV_SECRET" >&2; exit 1; }
COMMAND="dotnet /app/BrokerForge.Host.dll set-instance-secret --environment $ENVID --instance $INSTANCE --name $NAME --from kv:${VAULT_URI}secrets/$KV_SECRET"
# exec needs a terminal; script(1) gives it one when this runs from another tool. `az containerapp exec` does not pass
# the command's exit status back, so success is the command's own success line in the output.
OUT="$(mktemp)"
trap 'rm -f "$OUT"' EXIT
if [ -t 0 ]; then
  az containerapp exec -g "$RG" -n bf-dev-host --command "$COMMAND" | tee "$OUT" || true
else
  script -q /dev/null az containerapp exec -g "$RG" -n bf-dev-host --command "$COMMAND" | tee "$OUT" || true
fi
if grep -q "Set instance secret \`$NAME\` of $INSTANCE in environment $ENVID from " "$OUT"; then
  echo "OK: instance secret $NAME of $INSTANCE is set."
else
  echo "FAILED: the host did not confirm instance secret $NAME of $INSTANCE (see its output above)." >&2
  exit 1
fi
