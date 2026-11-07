#!/usr/bin/env bash
# Create or check one source's Dataverse components with `bf generate` (HLD D-22), against the dev environment.
# Usage: tools/dev/generate.sh <ServiceInstance> <SolutionUniqueName> [--dry-run] [--confirm <fingerprint>]
# Exit 4: the printed changes need confirming; run again with --confirm and the printed fingerprint.
# Reads the host URL from ~/.brokerforge/dev-ids.env and the host app ID and org URL from ~/.brokerforge/spike-ids.env.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
INSTANCE="${1:?service instance}"
SOLUTION="${2:?solution unique name}"
shift 2
# shellcheck disable=SC1090
source "${BF_HOME:-$HOME/.brokerforge}/spike-ids.env"
# shellcheck disable=SC1090
source "${BF_HOME:-$HOME/.brokerforge}/dev-ids.env"
ORG="${DV_ORG:?Set DV_ORG, or add DV_ORG=<org url> to ~/.brokerforge/spike-ids.env}"
dotnet run --project "$ROOT/tools/BrokerForge.Cli" -- generate --org "$ORG" \
  --schemas "$ROOT/schemas" --descriptors "$ROOT/descriptors" --instance "$INSTANCE" \
  --solution "$SOLUTION" --publisher brokerforgeext --host-url "$DEV_HOST_URL" --scope "api://$HOST_APP/.default" "$@"
