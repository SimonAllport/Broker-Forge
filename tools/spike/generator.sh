#!/usr/bin/env bash
# Portal connections plan 3, task 1: forms, views, dependencies, removals and roles on a generated virtual table.
# Throwaway probe, kept for the record.
#
#   tools/spike/generator.sh <stage>     stages: generate | read | layout | dependencies | role | api | cleanup
#
# Writes to the dev environment named by DV_ORG (from ~/.brokerforge/spike-ids.env), as the signed-in az CLI user, in
# solution BrokerForgeGeneratorSpike (publisher brokerforgeext). Instance SpikeAPI, table ext_spikeitem, Custom API
# ext_SpikeAdjust. Prints no org URL and no token.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
DV_SOLUTION="" source "$here/dv.sh"
stage="${1:?stage}"
export DV_ORG REPO="$repo" STAGE="$stage"
if [ "$stage" = generate ]; then
  work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
  mkdir -p "$work/schemas" "$work/descriptors/SpikeAPI"
  sed -e 's/ext_item/ext_spikeitem/g; s/ext_AdjustStock/ext_SpikeAdjust/g; s/^externalName: item/externalName: spikeitem/' \
    "$repo/schemas/InventoryAPI.yaml" > "$work/schemas/SpikeAPI.yaml"
  for f in "$repo"/descriptors/InventoryAPI/*.yaml; do
    sed -e 's/ext_item/ext_spikeitem/g; s/ext_AdjustStock/ext_SpikeAdjust/g; s/InventoryAPI/SpikeAPI/g' "$f" > "$work/descriptors/SpikeAPI/$(basename "$f")"
  done
  # shellcheck disable=SC1090
  source "$HOME/.brokerforge/spike-ids.env"; source "$HOME/.brokerforge/dev-ids.env"
  dotnet run --project "$repo/tools/BrokerForge.Cli" -- generate --org "$DV_ORG" --schemas "$work/schemas" --descriptors "$work/descriptors" \
    --instance SpikeAPI --solution BrokerForgeGeneratorSpike --publisher brokerforgeext --host-url "$DEV_HOST_URL" --scope "api://$HOST_APP/.default" \
    | sed "s#$DV_ORG#https://contoso.crm.dynamics.com#g"
  exit 0
fi
/usr/bin/python3 -I "$here/generator_probe.py"
