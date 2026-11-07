#!/usr/bin/env bash
# Export the BrokerForge core solution from dev and keep it as readable source in solution/BrokerForgeCore (spec 11,
# plan 4 task 7). Then pack the managed package other environments install with tools/dev/install-core.sh.
# Needs pac (dotnet tool install --global Microsoft.PowerApps.CLI.Tool); pac only unpacks and packs, it signs in nowhere.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PAC="$(command -v pac || echo "$HOME/.dotnet/tools/pac")"
PY="${PYTHON:-$( [ -x /usr/bin/python3 ] && echo /usr/bin/python3 || echo python3 )}"
OUT="$ROOT/out/core"
"$PY" "$ROOT/tools/dev/export_core.py" "$OUT"
# Both: the unpacked source holds the managed and unmanaged differences, so either package packs from it.
"$PAC" solution unpack --zipfile "$OUT/BrokerForgeCore.zip" --folder "$ROOT/solution/BrokerForgeCore" --packagetype Both --allowDelete true --allowWrite true --clobber true
# The assembly's managed identity is a record of this environment; install-core.sh binds the assembly to the target's own.
find "$ROOT/solution/BrokerForgeCore/PluginAssemblies" -name '*.data.xml' -exec sed -i.bak '/<ManagedIdentityId>/d' {} \; -exec rm -f {}.bak \;
# The repo is public: no build machine path may travel in the source (the plug-in builds with a PathMap).
if grep -rlaE '/(Users|home)/[^/]+/' "$ROOT/solution/BrokerForgeCore" >/dev/null; then
  echo "solution/BrokerForgeCore contains a local path:" >&2
  grep -rlaE '/(Users|home)/[^/]+/' "$ROOT/solution/BrokerForgeCore" >&2
  exit 1
fi
"$PAC" solution pack --zipfile "$ROOT/out/BrokerForgeCore_managed.zip" --folder "$ROOT/solution/BrokerForgeCore" --packagetype Managed
echo "source: solution/BrokerForgeCore; managed package: out/BrokerForgeCore_managed.zip"
