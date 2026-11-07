"""Install the BrokerForge core into another environment (spec 11, plan 4 task 7): import the managed package from
tools/dev/export-core.sh, then the per-environment steps that cannot travel in a solution:

- the managed identity record for the plug-in app (phase 0 findings section 4) and the assembly bound to it;
- this environment's values of bf_HostUrl and bf_HostScope (set outside the solution, so they never travel);
- the host's application user with the `BrokerForge host` role (the role itself arrives in the package).

It runs as the az CLI user against DV_ORG (set by install-core.sh). It does not do the steps outside Dataverse, which
install-core.sh prints: the plug-in app's federated credential for this environment, and the host's
Trust:AllowedCallers entry (a host deploy).

Usage: DV_ORG=<org url> [PLUGIN_APP=<this environment's plug-in app>] python3 install_core.py <managed zip>
Each environment needs its own plug-in app (tools/dev/setup-plugin-app.sh); PLUGIN_APP defaults to dev's.
"""
import base64, os, sys, uuid

from setup_dataverse import DEV, SPIKE, call, ensure_env_var, ensure_host_user, first, step

CORE = "BrokerForgeCore"


def main(package):
    step(f"import {package}")
    call("POST", "ImportSolution", {"CustomizationFile": base64.b64encode(open(package, "rb").read()).decode(),
                                    "OverwriteUnmanagedCustomizations": False, "PublishWorkflows": True, "ImportJobId": str(uuid.uuid4())})

    plugin_app = os.environ.get("PLUGIN_APP") or SPIKE["PLUGIN_APP"]
    step("managed identity for the plug-in app")
    mi = first(f"managedidentities?$select=managedidentityid&$filter=applicationid eq {plugin_app}")
    if mi:
        mi_id = mi["managedidentityid"]
    else:
        _, body, _ = call("POST", "managedidentities", {"applicationid": plugin_app, "tenantid": SPIKE["TENANT"], "credentialsource": 2,
                                                        "subjectscope": 1, "version": 2, "name": "BrokerForge plug-in"})
        mi_id = body["managedidentityid"]
    asm = first("pluginassemblies?$select=pluginassemblyid&$filter=name eq 'BrokerForge.Plugin'")
    call("PATCH", f"pluginassemblies({asm['pluginassemblyid']})", {"managedidentityid@odata.bind": f"/managedidentities({mi_id})"})

    step("environment variables bf_HostUrl and bf_HostScope (this environment's values)")
    ensure_env_var("bf_HostUrl", "BrokerForge host URL", DEV["DEV_HOST_URL"], CORE)
    ensure_env_var("bf_HostScope", "BrokerForge host token scope", f"api://{SPIKE['HOST_APP']}/.default", CORE)

    ensure_host_user(CORE)


if __name__ == "__main__":
    main(sys.argv[1])
