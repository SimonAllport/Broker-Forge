#!/usr/bin/env bash
# The plug-in identity for one more environment (plan 5, done criterion 4): an app registration with a single federated
# credential bound to that environment and the plug-in's signing certificate. Each
# environment needs its own, because the host tells environments apart only by the token's azp (HLD D-09).
# Writes SECOND_PLUGIN_APP to ~/.brokerforge/dev-ids.env. Run it before tools/dev/install-core.sh and tools/dev/deploy.sh.
# Usage: tools/dev/setup-plugin-app.sh <environment id>
set -euo pipefail
ENV_ID="${1:?environment id}"
OUT="${BF_HOME:-$HOME/.brokerforge}/dev-ids.env"
# shellcheck disable=SC1090
source "${BF_HOME:-$HOME/.brokerforge}/spike-ids.env"
: "${TENANT:?}"
NAME="bf-plugin-${ENV_ID:0:8}"
APP="$(az ad app list --display-name "$NAME" --query "[0].appId" -o tsv)"
[ -n "$APP" ] || APP="$(az ad app create --display-name "$NAME" --sign-in-audience AzureADMyOrg --query appId -o tsv)"
az ad sp show --id "$APP" >/dev/null 2>&1 || az ad sp create --id "$APP" >/dev/null
# Subject: /eid1/c/pub/t/<tenant as Base64URL of its little-endian bytes>/a/<Power Platform app>/n/plugin/e/<environment>/h/<cert SHA-256>.
TENANT_B64="$(/usr/bin/python3 -c "import base64,uuid,sys; print(base64.urlsafe_b64encode(uuid.UUID(sys.argv[1]).bytes_le).decode().rstrip('='))" "$TENANT")"
HASH="$(shasum -a 256 "${BF_HOME:-$HOME/.brokerforge}/spike-signing/cert.cer" | cut -d' ' -f1)"
SUBJECT="/eid1/c/pub/t/$TENANT_B64/a/qzXoWDkuqUa3l6zM5mM0Rw/n/plugin/e/$ENV_ID/h/$HASH"
if [ -z "$(az ad app federated-credential list --id "$APP" --query "[?subject=='$SUBJECT'].name" -o tsv)" ]; then
  az ad app federated-credential create --id "$APP" --parameters "{\"name\":\"dataverse-plugin\",\"issuer\":\"https://login.microsoftonline.com/$TENANT/v2.0\",\"subject\":\"$SUBJECT\",\"audiences\":[\"api://AzureADTokenExchange\"]}" >/dev/null
fi
touch "$OUT"
grep -v '^SECOND_PLUGIN_APP=' "$OUT" > "$OUT.tmp" || true
echo "SECOND_PLUGIN_APP=$APP" >> "$OUT.tmp"
mv "$OUT.tmp" "$OUT"
echo "plug-in app $NAME ready for environment $ENV_ID (SECOND_PLUGIN_APP in $OUT)"
