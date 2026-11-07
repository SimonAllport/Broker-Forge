"""Register the BrokerForge core components in the dev environment: the signed plug-in assembly, both
plug-in types, the bf_brokerdatasource table, the BrokerForge data provider and the bf_HostUrl and
bf_HostScope environment variables (HLD D-20). Source tables, data source records and Custom APIs come
from `bf generate` (tools/dev/generate.sh).

Idempotent: every step looks for its component first. Follows the phase 0 metadata rules. Uses the az CLI user's token; no secret is read or written.

Also creates the host managed identity's application user with the `BrokerForge host` role. The role reads
connection designs (web resources) and environment variables (HLD D-34), and has what Generate needs to run as
the host (HLD D-36): tables, columns, Custom APIs, forms, views, roles, solutions, web resources, environment
variables and data source records, plus publishing. That is close to System Customizer.

Also the admin app (tools/dev/core_admin.py): the bf_connection table, its form and the review page, the admin Custom
APIs, the BrokerForge administrator role and the BrokerForge model-driven app.

Usage: python3 setup_dataverse.py <signed plug-in dll>
       python3 setup_dataverse.py --host-user     (only the application user and role)
       python3 setup_dataverse.py --admin-app     (only the admin app, then the application user and role)
"""
import base64, json, os, subprocess, sys, urllib.error, urllib.parse, urllib.request

# The ID files live outside the repo: BF_HOME, or ~/.brokerforge (an install keeps its own, tools/install/install.sh).
BF_HOME = os.environ.get("BF_HOME") or os.path.expanduser("~/.brokerforge")
ORG = os.environ.get("DV_ORG") or next((l.split("=", 1)[1].strip().strip('"') for l in open(os.path.join(BF_HOME, "spike-ids.env"))
                                   if l.startswith("DV_ORG=")), None) or sys.exit("Set DV_ORG, or add DV_ORG=<org url> to ~/.brokerforge/spike-ids.env")
API = ORG + "/api/data/v9.2/"
DATA_SOURCE_PROVIDER = "b2112a7e-b26c-42f7-9b63-9a809a9d716f"
ASSEMBLY_VERSION = "0.1.0.0"
PUBLIC_KEY_TOKEN = "359afdf5b289a42f"


def ids(path):
    out = {}
    for line in open(os.path.expanduser(path)):
        if "=" in line and not line.startswith("#"):
            k, v = line.strip().split("=", 1)
            out[k] = v
    return out


SPIKE = ids(os.path.join(BF_HOME, "spike-ids.env"))
DEV_PATH = os.path.join(BF_HOME, "dev-ids.env")
DEV = ids(DEV_PATH)
TOKEN = subprocess.check_output(["az", "account", "get-access-token", "--resource", ORG, "--query", "accessToken", "-o", "tsv"], text=True).strip()


def call(method, path, body=None, solution=None, ok=(200, 201, 204), representation=True):
    headers = {"Authorization": "Bearer " + TOKEN, "Accept": "application/json", "Content-Type": "application/json; charset=utf-8",
               "OData-Version": "4.0", "OData-MaxVersion": "4.0"}
    if representation:
        headers["Prefer"] = "return=representation"
    if solution:
        headers["MSCRM.SolutionUniqueName"] = solution
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(API + urllib.parse.quote(path, safe="/?$=&'(),:@"), data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=300) as r:
            text = r.read().decode()
            return r.status, (json.loads(text) if text else {}), dict(r.headers)
    except urllib.error.HTTPError as e:
        text = e.read().decode()
        if e.code in ok:
            return e.code, {}, dict(e.headers)
        if e.code == 404 and 404 in ok:
            return 404, {}, {}
        sys.exit(f"{method} {path} -> HTTP {e.code}: {text[:1500]}")


def first(path):
    status, body, _ = call("GET", path, ok=(200, 404))
    rows = body.get("value", []) if status == 200 else []
    return rows[0] if rows else None


def label(text):
    return {"LocalizedLabels": [{"Label": text, "LanguageCode": 1033}]}


def mp(value, name):
    return {"Value": value, "CanBeChanged": False, "ManagedPropertyLogicalName": name}


def string_attr(schema, display, length, external, primary=False):
    a = {"@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata", "SchemaName": schema, "DisplayName": label(display),
         "RequiredLevel": {"Value": "None"}, "MaxLength": length, "FormatName": {"Value": "Text"}, "ExternalName": external}
    if primary:
        a["IsPrimaryName"] = True
    return a


VIRTUAL_FLAGS = {
    "OwnershipType": "OrganizationOwned", "HasActivities": False, "HasNotes": False, "IsActivity": False,
    "ChangeTrackingEnabled": False, "CanChangeTrackingBeEnabled": mp(False, "canchangetrackingbeenabled"),
    "CanCreateCharts": mp(False, "cancreatecharts"), "IsAuditEnabled": mp(False, "canmodifyauditsettings"),
    "IsDuplicateDetectionEnabled": mp(False, "canmodifyduplicatedetectionsettings"),
}


def step(text):
    print("==", text, flush=True)


def ensure_solution(unique, friendly, publisher_id):
    if not first(f"solutions?$select=solutionid&$filter=uniquename eq '{unique}'"):
        call("POST", "solutions", {"uniquename": unique, "friendlyname": friendly, "version": "1.0.0.0",
                                   "publisherid@odata.bind": f"/publishers({publisher_id})"})


def ensure_publisher(unique, prefix, option_prefix):
    row = first(f"publishers?$select=publisherid&$filter=uniquename eq '{unique}'")
    if row:
        return row["publisherid"]
    _, body, _ = call("POST", "publishers", {"uniquename": unique, "friendlyname": unique, "customizationprefix": prefix,
                                             "customizationoptionvalueprefix": option_prefix})
    return body["publisherid"]


def entity(logical):
    status, body, _ = call("GET", f"EntityDefinitions(LogicalName='{logical}')?$select=MetadataId,TableType,DataProviderId,DataSourceId", ok=(200, 404))
    return body if status == 200 else None


def ensure_attribute(table, payload, solution):
    logical = payload["SchemaName"].lower()
    status, _, _ = call("GET", f"EntityDefinitions(LogicalName='{table}')/Attributes(LogicalName='{logical}')?$select=LogicalName", ok=(200, 404))
    if status == 404:
        call("POST", f"EntityDefinitions(LogicalName='{table}')/Attributes", payload, solution)


def set_primary_key_external_name(table, external):
    path = f"EntityDefinitions(LogicalName='{table}')/Attributes(LogicalName='{table}id')"
    _, attr, _ = call("GET", path)
    if attr.get("ExternalName") == external:
        return
    attr = {k: v for k, v in attr.items() if not k.startswith("@odata.") or k == "@odata.type"}
    attr["@odata.type"] = "Microsoft.Dynamics.CRM.AttributeMetadata"
    attr["ExternalName"] = external
    call("PUT", path, attr)


def ensure_plugin_type(asm_id, typename, friendly, solution):
    step(f"plug-in type {typename}")
    pt = first(f"plugintypes?$select=plugintypeid&$filter=typename eq '{typename}' and _pluginassemblyid_value eq {asm_id}")
    if pt:
        return pt["plugintypeid"]
    _, body, _ = call("POST", "plugintypes", {"typename": typename, "name": typename, "friendlyname": friendly,
                                              "pluginassemblyid@odata.bind": f"/pluginassemblies({asm_id})"}, solution)
    return body["plugintypeid"]


def ensure_env_var(schema, display, value, solution):
    """A string environment variable (type 100000000) in the solution with no default, and this environment's value set
    outside it, so the value never travels: another environment sets its own (tools/dev/install-core.sh, plan 4)."""
    d = first(f"environmentvariabledefinitions?$select=environmentvariabledefinitionid,defaultvalue&$filter=schemaname eq '{schema}'")
    if d:
        def_id = d["environmentvariabledefinitionid"]
        if d.get("defaultvalue"):
            call("PATCH", f"environmentvariabledefinitions({def_id})", {"defaultvalue": None})
    else:
        _, body, _ = call("POST", "environmentvariabledefinitions", {"schemaname": schema, "displayname": display, "type": 100000000}, solution)
        def_id = body["environmentvariabledefinitionid"]
    v = first(f"environmentvariablevalues?$select=environmentvariablevalueid,value&$filter=_environmentvariabledefinitionid_value eq {def_id}")
    if not v:
        call("POST", "environmentvariablevalues", {"schemaname": schema, "value": value,
                                                   "EnvironmentVariableDefinitionId@odata.bind": f"/environmentvariabledefinitions({def_id})"})
    else:
        if v.get("value") != value:
            call("PATCH", f"environmentvariablevalues({v['environmentvariablevalueid']})", {"value": value})
        # Earlier setups created the value in the solution; take it out (component type 381) so it stays here.
        sol = first(f"solutions?$select=solutionid&$filter=uniquename eq '{solution}'")
        inside = sol and first(f"solutioncomponents?$select=solutioncomponentid&$filter=_solutionid_value eq {sol['solutionid']} and objectid eq {v['environmentvariablevalueid']}")
        if inside:
            # The action names the component by its own ID, in the solutioncomponentid field.
            call("POST", "RemoveSolutionComponent", {"SolutionComponent": {"@odata.type": "Microsoft.Dynamics.CRM.solutioncomponent",
                                                                           "solutioncomponentid": v["environmentvariablevalueid"]},
                                                     "ComponentType": 381, "SolutionUniqueName": solution})


HOST_ROLE = "BrokerForge host"
# The host reads connection designs (web resources) and their environment variables with its own identity (HLD D-34).
# environmentvariablevalue has no privilege of its own: its rows are read through the definition's (portal-1 findings).
# Generate runs as the host too (HLD D-36, granted on 4 November 2026): it creates, changes and deletes the
# components below, and sets the lookups (Append on the row, AppendTo on its target) named after them. A Custom API's
# request parameters and response properties have no privileges of their own: the Custom API's cover them.
_CRUD = ["Entity", "Attribute", "OptionSet", "CustomAPI", "Role",
         "SystemForm", "Query", "WebResource", "EnvironmentVariableDefinition", "Solution", "Publisher", "Customization", "EntityDataSource",
         "bf_brokerdatasource"]
HOST_PRIVILEGES = sorted({f"prv{v}{c}" for c in _CRUD for v in ("Read", "Create", "Write", "Delete")}
                         | {"prvPublishCustomization", "prvReadRelationship", "prvReadUser", "prvReadPluginType", "prvReadBusinessUnit", "prvReadEntityDataProvider",
                            "prvAppendRole", "prvAppendToBusinessUnit", "prvAppendCustomAPI", "prvAppendToPluginType",
                            "prvAppendToCustomAPI",
                            "prvAppendSolution", "prvAppendToPublisher", "prvAppendToEnvironmentVariableDefinition",
                            # The admin app (plan 4): connection rows read and written (progress), teams read for the administrator check.
                            "prvReadbf_connection", "prvWritebf_connection", "prvReadTeam",
                            # A new table gets its default control configuration (found creating one as the host, plan 4).
                            "prvCreateCustomControlDefaultConfig", "prvWriteCustomControlDefaultConfig", "prvDeleteCustomControlDefaultConfig",
                            # Creating a table queues system jobs, which the creator must be able to read and own (5 November 2026).
                            "prvReadAsyncOperation", "prvCreateAsyncOperation", "prvWriteAsyncOperation", "prvAppendAsyncOperation",
                            # Publishing a new table writes its system relationships.
                            "prvCreateRelationship", "prvWriteRelationship", "prvDeleteRelationship",
                            # Publishing a table reads its system charts.
                            "prvReadSavedQueryVisualizations",
                            # Not prvCreateSdkMessage or prvCreateSdkMessageProcessingStep: actions are the administrator's step (D-38),
                            # and Dataverse drops the step privilege from the host's effective rights anyway (portal-5 findings).
                            # Deleting a column reads its attribute maps (6 November 2026, done criterion 5).
                            "prvReadAttributeMap"})


def ensure_host_user(solution):
    """The host managed identity's application user, with only the BrokerForge host role (HOST_PRIVILEGES)."""
    step("application user for the host identity, role BrokerForge host")
    client_id = subprocess.check_output(["az", "identity", "show", "-g", DEV["RG"], "-n", DEV.get("PREFIX", "bf-dev") + "-id", "--query", "clientId", "-o", "tsv"], text=True).strip()
    bu = first("businessunits?$select=businessunitid&$filter=_parentbusinessunitid_value eq null")["businessunitid"]
    role = first(f"roles?$select=roleid&$filter=name eq '{HOST_ROLE}' and _businessunitid_value eq {bu}")
    if role:
        role_id = role["roleid"]
    else:
        _, body, _ = call("POST", "roles", {"name": HOST_ROLE, "businessunitid@odata.bind": f"/businessunits({bu})"}, solution)
        role_id = body["roleid"]
    granted = []
    for name in HOST_PRIVILEGES:
        privilege = first(f"privileges?$select=privilegeid&$filter=name eq '{name}'")
        if not privilege:
            sys.exit(f"privilege {name} not found")
        granted.append({"PrivilegeId": privilege["privilegeid"], "PrivilegeName": name, "Depth": "Global", "BusinessUnitId": bu})
    call("POST", f"roles({role_id})/Microsoft.Dynamics.CRM.AddPrivilegesRole", {"Privileges": granted})
    user = first(f"systemusers?$select=systemuserid&$filter=applicationid eq {client_id}")
    if user:
        user_id = user["systemuserid"]
    else:
        _, body, _ = call("POST", "systemusers", {"applicationid": client_id, "businessunitid@odata.bind": f"/businessunits({bu})",
                                                 "firstname": "BrokerForge", "lastname": "host"})
        user_id = body["systemuserid"]
    if not first(f"systemusers({user_id})/systemuserroles_association?$select=roleid&$filter=roleid eq {role_id}"):
        call("POST", f"systemusers({user_id})/systemuserroles_association/$ref", {"@odata.id": f"{API}roles({role_id})"})
    print(f"   host application user {user_id}, role {role_id}")


def main(dll_path):
    core = "BrokerForgeCore"

    step("publisher and core solution")
    bf_publisher = ensure_publisher("brokerforge", "bf", 10001)
    ensure_solution(core, "BrokerForge Core", bf_publisher)

    step(f"plug-in assembly {ASSEMBLY_VERSION}")
    content = base64.b64encode(open(dll_path, "rb").read()).decode()
    asm = first(f"pluginassemblies?$select=pluginassemblyid&$filter=name eq 'BrokerForge.Plugin' and version eq '{ASSEMBLY_VERSION}'")
    if asm:
        asm_id = asm["pluginassemblyid"]
        call("PATCH", f"pluginassemblies({asm_id})", {"content": content})
    else:
        _, body, _ = call("POST", "pluginassemblies", {"name": "BrokerForge.Plugin", "content": content, "version": ASSEMBLY_VERSION,
                                                       "culture": "neutral", "publickeytoken": PUBLIC_KEY_TOKEN,
                                                       "isolationmode": 2, "sourcetype": 0}, core)
        asm_id = body["pluginassemblyid"]
    call("PATCH", f"pluginassemblies({asm_id})", {"managedidentityid@odata.bind": f"/managedidentities({SPIKE['MI']})"})

    pt_id = ensure_plugin_type(asm_id, "BrokerForge.Plugin.ProviderPlugin", "BrokerForge provider", core)
    api_pt_id = ensure_plugin_type(asm_id, "BrokerForge.Plugin.CustomApiPlugin", "BrokerForge Custom API handler", core)

    step("data source table bf_brokerdatasource")
    if not entity("bf_brokerdatasource"):
        call("POST", "EntityDefinitions", dict(VIRTUAL_FLAGS, **{
            "@odata.type": "Microsoft.Dynamics.CRM.EntityMetadata", "SchemaName": "bf_brokerdatasource",
            "DisplayName": label("BrokerForge Data Source"), "DisplayCollectionName": label("BrokerForge Data Sources"),
            "ExternalName": "brokerdatasource", "ExternalCollectionName": "brokerdatasources", "DataProviderId": DATA_SOURCE_PROVIDER,
            "Attributes": [string_attr("bf_name", "Name", 100, "name", primary=True)]}), core)
    for schema, display, length, external in [("bf_hosturl", "Host URL", 500, "hosturl"), ("bf_scope", "Token scope", 200, "scope"),
                                              ("bf_serviceinstance", "Service instance", 100, "serviceinstance")]:
        ensure_attribute("bf_brokerdatasource", string_attr(schema, display, length, external), core)
    set_primary_key_external_name("bf_brokerdatasource", "id")

    step("data provider BrokerForge")
    dp = first("entitydataproviders?$select=entitydataproviderid&$filter=name eq 'BrokerForge'")
    plugins = {k: pt_id for k in ("retrieveplugin", "retrievemultipleplugin", "createplugin", "updateplugin", "deleteplugin")}
    if dp:
        dp_id = dp["entitydataproviderid"]
        call("PATCH", f"entitydataproviders({dp_id})", plugins)
    else:
        _, body, _ = call("POST", "entitydataproviders", dict(plugins, name="BrokerForge", datasourcelogicalname="bf_brokerdatasource"), core)
        dp_id = body["entitydataproviderid"]

    step("environment variables bf_HostUrl and bf_HostScope")
    ensure_env_var("bf_HostUrl", "BrokerForge host URL", DEV["DEV_HOST_URL"], core)
    ensure_env_var("bf_HostScope", "BrokerForge host token scope", f"api://{SPIKE['HOST_APP']}/.default", core)
    call("POST", "PublishXml", {"ParameterXml": "<importexportxml><entities><entity>bf_brokerdatasource</entity></entities></importexportxml>"})

    # The admin app before the host user: the host role needs the connection table's privileges, which the table creates.
    from core_admin import ensure_admin_app
    ensure_admin_app(core, api_pt_id)

    ensure_host_user(core)

    values = dict(DEV, DEV_ASM=asm_id, DEV_PT=pt_id, DEV_API_PT=api_pt_id, DEV_DP=dp_id)
    with open(DEV_PATH, "w") as f:
        f.writelines(f"{k}={v}\n" for k, v in values.items())
    print(f"done: assembly {asm_id}, plug-in types {pt_id} and {api_pt_id}, provider {dp_id}")


if __name__ == "__main__":
    # --host-user: only the host's application user and role, without re-registering the plug-in.
    # --admin-app: only the admin app (plan 4) and then the host user, with the plug-in type already registered.
    if sys.argv[1] == "--host-user":
        ensure_host_user("BrokerForgeCore")
    elif sys.argv[1] == "--host-variables":
        ensure_env_var("bf_HostUrl", "BrokerForge host URL", DEV["DEV_HOST_URL"], "BrokerForgeCore")
        ensure_env_var("bf_HostScope", "BrokerForge host token scope", f"api://{SPIKE['HOST_APP']}/.default", "BrokerForgeCore")
    elif sys.argv[1] == "--admin-app":
        from core_admin import ensure_admin_app
        ensure_admin_app("BrokerForgeCore", DEV["DEV_API_PT"])
        ensure_host_user("BrokerForgeCore")
    else:
        main(sys.argv[1])
