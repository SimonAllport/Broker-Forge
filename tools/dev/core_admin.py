"""The BrokerForge admin app in the core solution (portal connections plan 4, spec 5 and 6): the bf_connection table and
its form and view, the review and connections web resources, the admin Custom APIs, the `BrokerForge administrator`
role and the `BrokerForge` model-driven app. Called by setup_dataverse.py; every step looks for its component first.

The admin Custom APIs are served by the existing BrokerForge.Plugin.CustomApiPlugin: no plug-in code changes (spec 11).
Each has ExecutePrivilegeName prvWritebf_connection, so the platform refuses a caller without it; the host then checks
the caller holds the BrokerForge administrator role (HLD D-37).
"""
import base64, os, uuid
import xml.etree.ElementTree as ET

from setup_dataverse import call, first, label, step, string_attr

TABLE = "bf_connection"
ADMIN_ROLE = "BrokerForge administrator"
WEB_DIR = os.path.join(os.path.dirname(__file__), "..", "..", "src", "BrokerForge.WebResources")
WEB_FILES = {"review.html": 1, "review.js": 3, "review-model.js": 3, "connection.js": 3, "connections.html": 1, "connections.js": 3}
# bf_authmode values use the bf publisher's option prefix 10001 (src/BrokerForge.Host/Admin/ConnectionRow.cs, Modes).
AUTH_MODES = [(100010000, "Service account"), (100010001, "Static"), (100010002, "OAuth"), (100010003, "Impersonate"), (100010004, "SSO")]
STRINGS = [("bf_baseurl", "Base URL", 500), ("bf_statickind", "Static kind", 50), ("bf_headername", "Header name", 100),
           ("bf_queryparam", "Query parameter", 100), ("bf_username", "User name", 256), ("bf_scope", "Scope", 500),
           ("bf_ssokind", "SSO kind", 50), ("bf_authorizeurl", "Authorize URL", 500), ("bf_tokenurl", "Token URL", 500),
           ("bf_oauthscopes", "OAuth scopes", 1000), ("bf_clientid", "Client ID", 200), ("bf_prefix", "Table prefix", 20),
           ("bf_solution", "Solution", 100), ("bf_generationstate", "Generation state", 20), ("bf_generationjobid", "Generation job", 40)]
MEMOS = [("bf_openapi", "Swagger / OpenAPI", 1048576), ("bf_answers", "Answers", 100000), ("bf_generationresult", "Generation result", 100000)]
DATES = [("bf_generationstarted", "Generation started"), ("bf_generationfinished", "Generation finished")]
# Unique name -> (inputs as (name, type, optional), outputs). Type 12 is Guid, 10 is String.
APIS = {
    "bf_ReadDefinition": ([("ConnectionId", 12, False)], ["Review"]),
    "bf_Generate": ([("ConnectionId", 12, False), ("Fingerprint", 10, True)], ["JobId", "State"]),
    "bf_ListConnections": ([], ["Connections"]),
}
GENERAL = ["bf_name", "bf_baseurl", "bf_authmode", "bf_statickind", "bf_headername", "bf_queryparam", "bf_username", "bf_scope",
           "bf_ssokind", "bf_authorizeurl", "bf_tokenurl", "bf_oauthscopes", "bf_clientid", "bf_usesconnectapp", "bf_prefix", "bf_solution", "bf_openapi"]
READ_ONLY = ["bf_generationstate", "bf_generationstarted", "bf_generationfinished"]
CLASS_IDS = {"string": "{4273EDBD-AC1D-40d3-9FB2-095C621B552D}", "memo": "{E0DECE4B-6FC8-4a8f-A065-082708572369}",
             "bool": "{67FAC785-CD58-4f9f-ABB3-4B7DDC6ED5ED}", "datetime": "{5B773807-9FB2-42db-97C3-7A91EFF8ADFF}",
             "optionset": "{3EF39988-22BB-4f0b-BBBE-64B5A3748AEE}", "webresource": "{9FDF5F91-88B1-47f4-AD53-C11EFC01A01D}"}


def stable(name):
    """A GUID that is the same on every run, so re-running writes the same form XML."""
    return "{" + str(uuid.uuid5(uuid.NAMESPACE_URL, "brokerforge:" + name)) + "}"


def ensure_table(core):
    step(f"table {TABLE}")
    status, _, _ = call("GET", f"EntityDefinitions(LogicalName='{TABLE}')?$select=MetadataId", ok=(200, 404))
    if status == 404:
        call("POST", "EntityDefinitions", {
            "@odata.type": "Microsoft.Dynamics.CRM.EntityMetadata", "SchemaName": TABLE, "OwnershipType": "OrganizationOwned",
            "DisplayName": label("BrokerForge connection"), "DisplayCollectionName": label("BrokerForge connections"),
            "Description": label("A REST source set up in Dataverse: what the administrator typed. Holds no secret."),
            "HasActivities": False, "HasNotes": False, "IsActivity": False,
            "Attributes": [{k: v for k, v in string_attr("bf_name", "Name", 100, None, primary=True).items() if k != "ExternalName"}
                           | {"RequiredLevel": {"Value": "ApplicationRequired"}}]}, core)

    def attr(payload):
        logical = payload["SchemaName"]
        s, _, _ = call("GET", f"EntityDefinitions(LogicalName='{TABLE}')/Attributes(LogicalName='{logical}')?$select=LogicalName", ok=(200, 404))
        if s == 404:
            call("POST", f"EntityDefinitions(LogicalName='{TABLE}')/Attributes", payload, core)

    for schema, display, length in STRINGS:
        a = string_attr(schema, display, length, None)
        a.pop("ExternalName")
        attr(a)
    for schema, display, length in MEMOS:
        attr({"@odata.type": "Microsoft.Dynamics.CRM.MemoAttributeMetadata", "SchemaName": schema, "DisplayName": label(display),
              "RequiredLevel": {"Value": "None"}, "MaxLength": length, "Format": "TextArea"})
    for schema, display in DATES:
        attr({"@odata.type": "Microsoft.Dynamics.CRM.DateTimeAttributeMetadata", "SchemaName": schema, "DisplayName": label(display),
              "RequiredLevel": {"Value": "None"}, "Format": "DateAndTime", "DateTimeBehavior": {"Value": "UserLocal"}})
    attr({"@odata.type": "Microsoft.Dynamics.CRM.BooleanAttributeMetadata", "SchemaName": "bf_usesconnectapp", "DisplayName": label("Uses the connect app"),
          "RequiredLevel": {"Value": "None"}, "DefaultValue": False,
          "OptionSet": {"TrueOption": {"Value": 1, "Label": label("Yes")}, "FalseOption": {"Value": 0, "Label": label("No")}}})
    attr({"@odata.type": "Microsoft.Dynamics.CRM.PicklistAttributeMetadata", "SchemaName": "bf_authmode", "DisplayName": label("Authentication"),
          "RequiredLevel": {"Value": "ApplicationRequired"},
          "OptionSet": {"IsGlobal": False, "OptionSetType": "Picklist", "Options": [{"Value": v, "Label": label(t)} for v, t in AUTH_MODES]}})


def ensure_web_resources(core):
    step("web resources bf_/brokerforge/*")
    ids = {}
    for name, kind in WEB_FILES.items():
        unique = f"bf_/brokerforge/{name}"
        content = base64.b64encode(open(os.path.join(WEB_DIR, name), "rb").read()).decode()
        row = first(f"webresourceset?$select=webresourceid,content&$filter=name eq '{unique}'")
        if row:
            ids[name] = row["webresourceid"]
            if row.get("content") != content:
                call("PATCH", f"webresourceset({row['webresourceid']})", {"content": content})
        else:
            _, body, _ = call("POST", "webresourceset", {"name": unique, "displayname": f"BrokerForge {name}", "webresourcetype": kind, "content": content}, core)
            ids[name] = body["webresourceid"]
    xml = "".join(f"<webresource>{{{i}}}</webresource>" for i in ids.values())
    call("POST", "PublishXml", {"ParameterXml": f"<importexportxml><webresources>{xml}</webresources></importexportxml>"})
    return ids


def kind_of(column):
    if column in ("bf_openapi",):
        return "memo"
    if column == "bf_authmode":
        return "optionset"
    if column == "bf_usesconnectapp":
        return "bool"
    if column in ("bf_generationstarted", "bf_generationfinished"):
        return "datetime"
    return "string"


def form_xml(existing, labels, review_id):
    root = ET.fromstring(existing)
    for tag in ("tabs", "formLibraries", "events"):
        for old in root.findall(tag):
            root.remove(old)

    def cell(column, disabled=False):
        c = ET.Element("cell", id=stable(f"{TABLE}.{column}"))
        ET.SubElement(ET.SubElement(c, "labels"), "label", description=labels.get(column, column), languagecode="1033")
        control = ET.SubElement(c, "control", id=column, classid=CLASS_IDS[kind_of(column)], datafieldname=column)
        if disabled:
            control.set("disabled", "true")
        return c

    def tab(name, title, cells):
        t = ET.Element("tab", name=name, id=stable(f"{TABLE}.tab.{name}"), expanded="true", showlabel="true", verticallayout="true")
        ET.SubElement(ET.SubElement(t, "labels"), "label", description=title, languagecode="1033")
        section = ET.SubElement(ET.SubElement(ET.SubElement(t, "columns"), "column", width="100%"), "sections")
        s = ET.SubElement(section, "section", name=f"{name}_section", id=stable(f"{TABLE}.section.{name}"), showlabel="false", showbar="false", columns="1")
        ET.SubElement(ET.SubElement(s, "labels"), "label", description=title, languagecode="1033")
        rows = ET.SubElement(s, "rows")
        for c in cells:
            ET.SubElement(rows, "row").append(c)
        return t

    review = ET.Element("cell", id=stable(f"{TABLE}.review"), showlabel="false", rowspan="40")
    ET.SubElement(ET.SubElement(review, "labels"), "label", description="Review", languagecode="1033")
    control = ET.SubElement(review, "control", id="WebResource_review", classid=CLASS_IDS["webresource"])
    params = ET.SubElement(control, "parameters")
    for k, v in [("Url", "bf_/brokerforge/review.html"), ("PassParameters", "false"), ("Security", "false"), ("Scrolling", "auto"),
                 ("Border", "false"), ("WebResourceId", "{" + review_id + "}")]:
        ET.SubElement(params, k).text = v

    tabs = ET.Element("tabs")
    tabs.append(tab("general", "Connection", [cell(c) for c in GENERAL]))
    tabs.append(tab("review", "Review and generate", [review] + [cell(c, disabled=True) for c in READ_ONLY]))
    root.insert(0, tabs)
    libraries = ET.SubElement(root, "formLibraries")
    ET.SubElement(libraries, "Library", name="bf_/brokerforge/connection.js", libraryUniqueId=stable("library.connection"))
    events = ET.SubElement(root, "events")
    handlers = ET.SubElement(ET.SubElement(events, "event", name="onload", application="false", active="false"), "Handlers")
    ET.SubElement(handlers, "Handler", functionName="BrokerForge.Connection.onLoad", libraryName="bf_/brokerforge/connection.js",
                  handlerUniqueId=stable("handler.onload"), enabled="true", parameters="", passExecutionContext="true")
    return ET.tostring(root, encoding="unicode")


def ensure_form_and_view(web_ids):
    step(f"{TABLE} main form and view")
    form = first(f"systemforms?$select=formid,formxml&$filter=objecttypecode eq '{TABLE}' and type eq 2")
    if not form:
        raise SystemExit(f"{TABLE} has no main form yet; publish the table and run again")
    _, attrs, _ = call("GET", f"EntityDefinitions(LogicalName='{TABLE}')/Attributes?$select=LogicalName,DisplayName")
    labels = {a["LogicalName"]: ((a.get("DisplayName") or {}).get("UserLocalizedLabel") or {}).get("Label", a["LogicalName"]) for a in attrs["value"]}
    xml = form_xml(form["formxml"], labels, web_ids["review.html"])
    if xml != form["formxml"]:
        call("PATCH", f"systemforms({form['formid']})", {"formxml": xml})
    view = first(f"savedqueries?$select=savedqueryid,layoutxml,fetchxml&$filter=returnedtypecode eq '{TABLE}' and querytype eq 0 and isdefault eq true")
    if view:
        cols = ["bf_name", "bf_baseurl", "bf_generationstate", "bf_generationfinished"]
        fetch = (f'<fetch version="1.0" mapping="logical"><entity name="{TABLE}"><attribute name="{TABLE}id"/>'
                 + "".join(f'<attribute name="{c}"/>' for c in cols) + '<order attribute="bf_name" descending="false"/></entity></fetch>')
        layout = (f'<grid name="resultset" object="0" jump="bf_name" select="1" icon="1" preview="1"><row name="result" id="{TABLE}id">'
                  + "".join(f'<cell name="{c}" width="200"/>' for c in cols) + "</row></grid>")
        if view.get("fetchxml") != fetch or view.get("layoutxml") != layout:
            call("PATCH", f"savedqueries({view['savedqueryid']})", {"fetchxml": fetch, "layoutxml": layout})
    call("POST", "PublishXml", {"ParameterXml": f"<importexportxml><entities><entity>{TABLE}</entity></entities></importexportxml>"})


def ensure_custom_apis(core, plugin_type_id):
    for unique, (inputs, outputs) in APIS.items():
        step(f"Custom API {unique}")
        if first(f"customapis?$select=customapiid&$filter=uniquename eq '{unique}'"):
            continue
        call("POST", "customapis", {
            "uniquename": unique, "name": unique, "displayname": unique, "description": f"BrokerForge admin: {unique}",
            "bindingtype": 0, "isfunction": False, "isprivate": False, "allowedcustomprocessingsteptype": 0,
            "executeprivilegename": f"prvWrite{TABLE}",
            "PluginTypeId@odata.bind": f"/plugintypes({plugin_type_id})",
            "CustomAPIRequestParameters": [{"uniquename": n, "name": f"{unique}.{n}", "displayname": n, "description": n, "type": t, "isoptional": o}
                                           for n, t, o in inputs],
            "CustomAPIResponseProperties": [{"uniquename": n, "name": f"{unique}.{n}", "displayname": n, "description": n, "type": 10} for n in outputs],
        }, core)


def ensure_admin_role(core):
    step(f"role {ADMIN_ROLE}")
    bu = first("businessunits?$select=businessunitid&$filter=_parentbusinessunitid_value eq null")["businessunitid"]
    role = first(f"roles?$select=roleid&$filter=name eq '{ADMIN_ROLE}' and _businessunitid_value eq {bu}")
    if role:
        role_id = role["roleid"]
    else:
        _, body, _ = call("POST", "roles", {"name": ADMIN_ROLE, "businessunitid@odata.bind": f"/businessunits({bu})",
                                            "description": "May set up BrokerForge connections: create, read again, generate."}, core)
        role_id = body["roleid"]
    names = [f"prv{v}{TABLE}" for v in ("Create", "Read", "Write", "Delete", "Append", "AppendTo")]
    granted = []
    for name in names:
        privilege = first(f"privileges?$select=privilegeid&$filter=name eq '{name}'")
        if not privilege:
            raise SystemExit(f"privilege {name} not found")
        granted.append({"PrivilegeId": privilege["privilegeid"], "PrivilegeName": name, "Depth": "Global", "BusinessUnitId": bu})
    call("POST", f"roles({role_id})/Microsoft.Dynamics.CRM.AddPrivilegesRole", {"Privileges": granted})
    return role_id


def ensure_app(core, web_ids):
    step("model-driven app BrokerForge")
    sitemap_xml = (
        '<SiteMap IntroducedVersion="7.0.0.0"><Area Id="bf_area" ShowGroups="true"><Titles><Title LCID="1033" Title="BrokerForge"/></Titles>'
        '<Group Id="bf_group"><Titles><Title LCID="1033" Title="Connections"/></Titles>'
        '<SubArea Id="bf_connections_page" Url="$webresource:bf_/brokerforge/connections.html"><Titles><Title LCID="1033" Title="Overview"/></Titles></SubArea>'
        f'<SubArea Id="bf_connection_table" Entity="{TABLE}"><Titles><Title LCID="1033" Title="Connections"/></Titles></SubArea>'
        "</Group></Area></SiteMap>")
    sitemap = first("sitemaps?$select=sitemapid,sitemapxml&$filter=sitemapnameunique eq 'bf_BrokerForge'")
    if sitemap:
        sitemap_id = sitemap["sitemapid"]
        if sitemap.get("sitemapxml") != sitemap_xml:
            call("PATCH", f"sitemaps({sitemap_id})", {"sitemapxml": sitemap_xml})
    else:
        _, body, _ = call("POST", "sitemaps", {"sitemapname": "BrokerForge", "sitemapnameunique": "bf_BrokerForge", "sitemapxml": sitemap_xml}, core)
        sitemap_id = body["sitemapid"]
    def find_app():
        # An app is not readable as a row until it is published, so look among the unpublished ones too.
        return (first("appmodules?$select=appmoduleid&$filter=uniquename eq 'bf_BrokerForge'")
                or first("appmodules/Microsoft.Dynamics.CRM.RetrieveUnpublishedMultiple()?$select=appmoduleid&$filter=uniquename eq 'bf_BrokerForge'"))
    app = find_app()
    if not app:
        call("POST", "appmodules", {"name": "BrokerForge", "uniquename": "bf_BrokerForge", "clienttype": 4, "navigationtype": 0,
                                    "description": "Set up BrokerForge connections.", "webresourceid": "953b9fac-1e5e-e611-80d6-00155ded156f"}, core,
             representation=False)
        app = find_app()
    app_id = app["appmoduleid"]
    entity = call("GET", f"EntityDefinitions(LogicalName='{TABLE}')?$select=MetadataId")[1]["MetadataId"]
    call("POST", "AddAppComponents", {"AppId": app_id, "Components": [
        {"@odata.type": "Microsoft.Dynamics.CRM.sitemap", "sitemapid": sitemap_id},
        {"@odata.type": "Microsoft.Dynamics.CRM.entity", "entityid": entity},
    ]})
    call("POST", "PublishXml", {"ParameterXml": f"<importexportxml><appmodules><appmodule>{app_id}</appmodule></appmodules>"
                                                f"<sitemaps><sitemap>{sitemap_id}</sitemap></sitemaps></importexportxml>"})
    return app_id


def ensure_admin_app(core, plugin_type_id):
    ensure_table(core)
    call("POST", "PublishXml", {"ParameterXml": f"<importexportxml><entities><entity>{TABLE}</entity></entities></importexportxml>"})
    web_ids = ensure_web_resources(core)
    ensure_form_and_view(web_ids)
    ensure_custom_apis(core, plugin_type_id)
    ensure_admin_role(core)
    ensure_app(core, web_ids)
