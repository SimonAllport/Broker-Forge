#!/usr/bin/env bash
# Phase 0 Web API helper (bash and zsh). Source it, then: dv GET 'publishers?$select=uniquename'
# Token comes from the signed-in az CLI user; it is never printed.
# dvjson extracts one field from a dv response body: dv POST ... | dvjson publisherid
# The org URL is not in this public repo: DV_ORG from the environment, else from ~/.brokerforge/spike-ids.env.
[ -z "${DV_ORG:-}" ] && [ -f "${BF_HOME:-$HOME/.brokerforge}/spike-ids.env" ] && DV_ORG="$(sed -n 's/^DV_ORG=//p' "${BF_HOME:-$HOME/.brokerforge}/spike-ids.env" | tr -d '"')"
: "${DV_ORG:?Set DV_ORG, or add DV_ORG=<org url> to ~/.brokerforge/spike-ids.env}"
DV_SOLUTION="${DV_SOLUTION-BrokerForgeSpike}"

dv() {
  local method="$1" resource="$2" body="${3:-}"
  local token
  token="$(az account get-access-token --resource "$DV_ORG" --query accessToken -o tsv)" || return 1
  local -a args
  args=(-sS -X "$method" "$DV_ORG/api/data/v9.2/$resource"
    -H "Authorization: Bearer $token"
    -H "Accept: application/json" -H "Content-Type: application/json; charset=utf-8"
    -H "OData-Version: 4.0" -H "OData-MaxVersion: 4.0"
    -H "Prefer: return=representation"
    -w '\nHTTP %{http_code}\n')
  [ -n "$DV_SOLUTION" ] && args+=(-H "MSCRM.SolutionUniqueName: $DV_SOLUTION")
  [ -n "$body" ] && args+=(--data-binary "$body")
  curl "${args[@]}"
}

dvjson() {
  python3 -c 'import sys,json
raw=sys.stdin.read(); body,_,status=raw.rpartition("\nHTTP ")
if not status.strip().startswith("2"): sys.exit("HTTP "+status.strip()+": "+body[:2000])
print(json.loads(body)[sys.argv[1]])' "$1"
}
