#!/usr/bin/env bash
# Run Generate for one connection as the dev host (HLD D-36), from the design already published in the environment.
# Stopgap until plan 4's bf_Generate.
# Usage: tools/dev/generate-in-host.sh <Instance> <Solution> [--preview | --confirm <fingerprint> | --with-role]
#   e.g. tools/dev/generate-in-host.sh WarehouseAPI BrokerForgeWarehouse --preview
# Without --with-role it runs inside the running host container, as the host's own identity. That identity can't grant
# a generated table's privileges (portal-3 findings section 3), so the role is left to an administrator and printed
# as pending (exit 3). --with-role runs the same admin command locally as the az CLI user (an administrator), with the
# container's Trust settings and the repo's descriptor folders, and applies the role too.
# Exit codes follow the admin command: 0 done or previewed, 1 failed, 3 done except the role (run again with
# --with-role), 4 the changes need confirming (run again with the printed --confirm).
set -euo pipefail
INSTANCE="${1:?service instance}"
SOLUTION="${2:?solution unique name}"
shift 2
EXTRA=""
LOCAL=""
case "${1:-}" in
  "") ;;
  --preview) EXTRA="--preview" ;;
  --confirm) EXTRA="--confirm ${2:?fingerprint}" ;;
  --with-role) LOCAL=1; EXTRA="--with-role" ;;
  *) echo "usage: $0 <Instance> <Solution> [--preview | --confirm <fingerprint> | --with-role]" >&2; exit 2 ;;
esac
# shellcheck disable=SC1090
source "${BF_HOME:-$HOME/.brokerforge}/spike-ids.env"
# shellcheck disable=SC1090
source "${BF_HOME:-$HOME/.brokerforge}/dev-ids.env"
: "${ENVID:?} ${RG:?}"
ARGS="generate --environment $ENVID --instance $INSTANCE --solution $SOLUTION --publisher brokerforgeext --from-environment $EXTRA"
OUT="$(mktemp)"
trap 'rm -f "$OUT"' EXIT
if [ -n "$LOCAL" ]; then
  ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
  while IFS=$'\t' read -r name value; do export "$name=$value"; done \
    < <(az containerapp show -g "$RG" -n bf-dev-host --query "properties.template.containers[0].env[?starts_with(name,'Trust__')].[name,value]" -o tsv)
  export Config__DescriptorsPath="$ROOT/descriptors" Config__SchemasPath="$ROOT/schemas"
  # shellcheck disable=SC2086
  dotnet run --project "$ROOT/src/BrokerForge.Host" -- $ARGS 2>&1 | grep -v '"EventId"' | tee "$OUT" || true
else
  # exec needs a terminal; script(1) gives it one when this runs from another tool. `az containerapp exec` does not
  # pass the command's exit status back, so the result is read from the command's own lines.
  COMMAND="dotnet /app/BrokerForge.Host.dll $ARGS"
  if [ -t 0 ]; then
    az containerapp exec -g "$RG" -n bf-dev-host --command "$COMMAND" | tee "$OUT" || true
  else
    script -q /dev/null az containerapp exec -g "$RG" -n bf-dev-host --command "$COMMAND" | tee "$OUT" || true
  fi
fi
if grep -q "run again with --confirm" "$OUT"; then
  exit 4
elif grep -q "^$INSTANCE: Succeeded" "$OUT" && grep -q "^$INSTANCE: needs an administrator" "$OUT"; then
  echo "DONE EXCEPT THE ROLE: run $0 $INSTANCE $SOLUTION --with-role as an administrator." >&2
  exit 3
elif grep -q "^$INSTANCE: Succeeded" "$OUT" || { [ "$EXTRA" = "--preview" ] && grep -Eq "^$INSTANCE: (changes|no changes)" "$OUT"; }; then
  echo "OK: generate $INSTANCE ${EXTRA:+($EXTRA) }finished."
else
  echo "FAILED: the host did not report success for $INSTANCE (see its output above)." >&2
  exit 1
fi
