#!/usr/bin/env bash
# Phase 3: the Entra app registrations the per-user sources need (HLD D-26, D-31). Safe to re-run; writes the
# IDs to ~/.brokerforge/dev-ids.env for tools/dev/deploy.sh. Run after deploy.sh has created the environment.
#   BrokerForge Connect (dev)          users sign in to the connect page with it, and it is the OAuth client of
#                                      the Impersonate flow. It has no secret: the host's managed identity is
#                                      its federated credential.
#                                      It also signs administrators in for Dataverse on the host's role page
#                                      (HLD D-36): delegated Dynamics CRM user_impersonation (5 November 2026).
#   BrokerForge Tickets mock API (dev) the Entra resource the Impersonate source calls (scope user_impersonation).
# Optional: TICKETS_BOB_OID=<object ID of a second test user> maps that user to the mock's bob.
set -euo pipefail

OUT="${BF_HOME:-$HOME/.brokerforge}/dev-ids.env"
# shellcheck disable=SC1090
source "${BF_HOME:-$HOME/.brokerforge}/spike-ids.env"
# shellcheck disable=SC1090
source "$OUT"
: "${TENANT:?} ${RG:?} ${DEV_HOST_URL:?}"

setvar() {
  local key="$1" value="$2" tmp
  tmp="$(mktemp)"
  grep -v "^$key=" "$OUT" > "$tmp" || true
  echo "$key=$value" >> "$tmp"
  mv "$tmp" "$OUT"
}

object_id() { az ad app show --id "$1" --query id -o tsv; }
graph_patch() { az rest --method PATCH --url "https://graph.microsoft.com/v1.0/applications/$(object_id "$1")" --headers Content-Type=application/json --body "$2" -o none; }
ensure_sp() { az ad sp show --id "$1" --query id -o tsv >/dev/null 2>&1 || az ad sp create --id "$1" -o none; }

echo "== Tickets mock API app"
if [ -z "${TICKETS_API_APP:-}" ]; then
  TICKETS_API_APP="$(az ad app create --display-name "BrokerForge Tickets mock API (dev)" --sign-in-audience AzureADMyOrg --query appId -o tsv)"
  setvar TICKETS_API_APP "$TICKETS_API_APP"
fi
SCOPE_ID="$(az ad app show --id "$TICKETS_API_APP" --query "api.oauth2PermissionScopes[?value=='user_impersonation'].id | [0]" -o tsv)"
if [ -z "$SCOPE_ID" ] || [ "$SCOPE_ID" = None ]; then
  SCOPE_ID="$(uuidgen | tr 'A-Z' 'a-z')"
  graph_patch "$TICKETS_API_APP" "{\"identifierUris\":[\"api://$TICKETS_API_APP\"],\"api\":{\"requestedAccessTokenVersion\":2,\"oauth2PermissionScopes\":[{\"id\":\"$SCOPE_ID\",\"value\":\"user_impersonation\",\"type\":\"User\",\"isEnabled\":true,\"adminConsentDisplayName\":\"Read your Tickets mock data\",\"adminConsentDescription\":\"BrokerForge reads and creates Tickets mock data as you.\",\"userConsentDisplayName\":\"Read your Tickets mock data\",\"userConsentDescription\":\"BrokerForge reads and creates Tickets mock data as you.\"}]}}"
fi
ensure_sp "$TICKETS_API_APP"

echo "== BrokerForge Connect app"
if [ -z "${CONNECT_APP:-}" ]; then
  CONNECT_APP="$(az ad app create --display-name "BrokerForge Connect (dev)" --sign-in-audience AzureADMyOrg --query appId -o tsv)"
  setvar CONNECT_APP "$CONNECT_APP"
fi
# Graph openid, profile, offline_access (delegated), the Tickets API scope, and Dynamics CRM user_impersonation for the
# role page, which sets <Instance> user with the signed-in administrator's own Dataverse token.
GRAPH=00000003-0000-0000-c000-000000000000
DYNAMICS=00000007-0000-0000-c000-000000000000
DYNAMICS_USER_IMPERSONATION=78ce3f0f-a1ce-49c2-8cde-64b5c0896db4
graph_patch "$CONNECT_APP" "{\"web\":{\"redirectUris\":[\"$DEV_HOST_URL/signin-oidc\",\"$DEV_HOST_URL/connect/callback\",\"$DEV_HOST_URL/admin/callback\"]},\"requiredResourceAccess\":[{\"resourceAppId\":\"$GRAPH\",\"resourceAccess\":[{\"id\":\"37f7f235-527c-4136-accd-4a02d197296e\",\"type\":\"Scope\"},{\"id\":\"14dad69e-099b-42c9-810b-d002981feec1\",\"type\":\"Scope\"},{\"id\":\"7427e0e9-2fba-42fe-b0c0-848c9e6a8182\",\"type\":\"Scope\"}]},{\"resourceAppId\":\"$TICKETS_API_APP\",\"resourceAccess\":[{\"id\":\"$SCOPE_ID\",\"type\":\"Scope\"}]},{\"resourceAppId\":\"$DYNAMICS\",\"resourceAccess\":[{\"id\":\"$DYNAMICS_USER_IMPERSONATION\",\"type\":\"Scope\"}]}]}"
ensure_sp "$CONNECT_APP"

MI_PRINCIPAL="$(az identity show -g "$RG" -n bf-dev-id --query principalId -o tsv)"
if [ -z "$(az ad app federated-credential list --id "$CONNECT_APP" --query "[?name=='bf-host-mi'].id | [0]" -o tsv)" ]; then
  az ad app federated-credential create --id "$CONNECT_APP" -o none --parameters \
    "{\"name\":\"bf-host-mi\",\"issuer\":\"https://login.microsoftonline.com/$TENANT/v2.0\",\"subject\":\"$MI_PRINCIPAL\",\"audiences\":[\"api://AzureADTokenExchange\"],\"description\":\"The BrokerForge host's managed identity authenticates the connect app.\"}"
  echo "   federated credential for $MI_PRINCIPAL created"
fi
# Users can consent at sign-in; admin consent just saves them the prompt where the signed-in user may grant it.
az ad app permission admin-consent --id "$CONNECT_APP" -o none 2>/dev/null && echo "   admin consent granted" || echo "   admin consent not granted: each user consents at first sign-in"

ALICE="${TICKETS_ALICE_OID:-$(az ad signed-in-user show --query id -o tsv)}"
setvar TICKETS_ALICE_OID "$ALICE"
[ -n "${TICKETS_BOB_OID:-}" ] && setvar TICKETS_BOB_OID "$TICKETS_BOB_OID"
echo "done: CONNECT_APP, TICKETS_API_APP and the Tickets users are in $OUT; run tools/dev/deploy.sh"
