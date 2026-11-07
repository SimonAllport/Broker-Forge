"""Export solution BrokerForgeCore from dev, unmanaged and managed, into out/ (plan 4 task 7), with the az CLI user's token.
tools/dev/export-core.sh unpacks them into solution/BrokerForgeCore. No value that is particular to dev travels: the
host variables' values and the host application user are set per environment by tools/dev/install-core.sh.

Usage: python3 export_core.py <out dir>
"""
import base64, os, sys

from setup_dataverse import call, step

out = sys.argv[1]
os.makedirs(out, exist_ok=True)
for managed, name in [(False, "BrokerForgeCore.zip"), (True, "BrokerForgeCore_managed.zip")]:
    step(f"export BrokerForgeCore ({'managed' if managed else 'unmanaged'})")
    _, body, _ = call("POST", "ExportSolution", {"SolutionName": "BrokerForgeCore", "Managed": managed})
    with open(os.path.join(out, name), "wb") as f:
        f.write(base64.b64decode(body["ExportSolutionFile"]))
    print(f"   {name}: {os.path.getsize(os.path.join(out, name))} bytes")
