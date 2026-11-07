# Driven by tools/spike/generator.sh; see there. Prints no org URL and no token.
import json, os, subprocess, sys, time, urllib.error, urllib.parse, urllib.request

ORG = os.environ["DV_ORG"].rstrip("/")
API = ORG + "/api/data/v9.2/"
SOLUTION = "BrokerForgeGeneratorSpike"
TABLE = "ext_spikeitem"
STAGE = os.environ["STAGE"]

def say(*a):
    print(" ".join(str(x) for x in a).replace(ORG, "https://contoso.crm.dynamics.com"), flush=True)

_tok = {"v": None}
def token():
    if not _tok["v"]:
        _tok["v"] = subprocess.check_output(["az", "account", "get-access-token", "--resource", ORG, "--query", "accessToken", "-o", "tsv"], text=True).strip()
    return _tok["v"]

class DvError(Exception):
    pass

def dv(method, resource, body=None, solution=SOLUTION):
    data = None if body is None else json.dumps(body).encode("utf-8")
    req = urllib.request.Request(API + urllib.parse.quote(resource, safe="/?&=$(),'@.:;_-*"), data=data, method=method)
    for k, v in {"Authorization": "Bearer " + token(), "Accept": "application/json", "Content-Type": "application/json; charset=utf-8",
                 "OData-Version": "4.0", "OData-MaxVersion": "4.0", "Prefer": "return=representation"}.items():
        req.add_header(k, v)
    if solution:
        req.add_header("MSCRM.SolutionUniqueName", solution)
    try:
        with urllib.request.urlopen(req, timeout=600) as r:
            raw = r.read()
            return json.loads(raw) if raw else {}
    except urllib.error.HTTPError as e:
        text = e.read().decode("utf-8", "replace")
        try:
            text = json.loads(text)["error"]["message"]
        except Exception:
            pass
        raise DvError(f"HTTP {e.code}: {text[:600]}")

def rows(resource):
    return dv("GET", resource, solution=None)["value"]

def forms():
    return rows(f"systemforms?$select=formid,name,type,isdefault,formactivationstate,formxml&$filter=objecttypecode eq '{TABLE}'")

def views():
    return rows(f"savedqueries?$select=savedqueryid,name,querytype,isdefault,iscustomizable,fetchxml,layoutxml&$filter=returnedtypecode eq '{TABLE}'")

def attribute_id(column):
    return dv("GET", f"EntityDefinitions(LogicalName='{TABLE}')/Attributes(LogicalName='{column}')?$select=MetadataId", solution=None)["MetadataId"]

def dependencies(component_type, object_id):
    return dv("GET", f"RetrieveDependenciesForDelete(ObjectId={object_id},ComponentType={component_type})", solution=None)["value"]

if STAGE == "read":
    for f in forms():
        say("FORM", f["name"], "type", f["type"], "default", f["isdefault"], "state", f["formactivationstate"], f["formid"])
        say("   ", f["formxml"][:1500])
    for v in views():
        say("VIEW", v["name"], "querytype", v["querytype"], "default", v["isdefault"], v["savedqueryid"])
        say("   fetch", v["fetchxml"])
        say("   layout", v["layoutxml"])

COLUMNS = ["ext_name", "ext_sku", "ext_onhand", "ext_price", "ext_state", "ext_updatedat"]

def publish():
    dv("POST", "PublishXml", {"ParameterXml": f"<importexportxml><entities><entity>{TABLE}</entity></entities></importexportxml>"}, solution=None)

def main_form():
    return next(f for f in forms() if f["type"] == 2)

def default_view():
    return next(v for v in views() if v["querytype"] == 0 and v["isdefault"])

def layout(columns):
    import re, uuid
    f = main_form()
    cells = "".join(f'<row><cell id="{{{uuid.uuid4()}}}"><labels><label description="{c}" languagecode="1033" /></labels>'
                    f'<control id="{c}" classid="{{4273EDBD-AC1D-40d3-9FB2-095C621B552D}}" datafieldname="{c}" /></cell></row>' for c in columns)
    formxml = re.sub(r"<rows>.*</rows>", "<rows>" + cells + "</rows>", f["formxml"], count=1, flags=re.S)
    dv("PATCH", f"systemforms({f['formid']})", {"formxml": formxml}, solution=None)
    v = default_view()
    attrs = "".join(f'<attribute name="{c}" />' for c in columns)
    fetch = re.sub(r"<entity name=\"[^\"]+\">.*</entity>", f'<entity name="{TABLE}"><attribute name="{TABLE}id" />{attrs}<order attribute="ext_name" descending="false" /></entity>', v["fetchxml"], flags=re.S)
    grid = re.sub(r"<row name=\"result\" id=\"[^\"]+\">.*</row>", f'<row name="result" id="{TABLE}id">' + "".join(f'<cell name="{c}" width="150" />' for c in columns) + "</row>", v["layoutxml"], flags=re.S)
    dv("PATCH", f"savedqueries({v['savedqueryid']})", {"fetchxml": fetch, "layoutxml": grid}, solution=None)
    publish()
    f, v = main_form(), default_view()
    say("form fields now:", re.findall(r'datafieldname="([^"]+)"', f["formxml"]))
    say("view cells now:", re.findall(r'<cell name="([^"]+)"', v["layoutxml"]), "fetch attrs:", re.findall(r'<attribute name="([^"]+)"', v["fetchxml"]))

if STAGE == "layout":
    t = time.time(); layout(COLUMNS); say(f"layout + publish {time.time() - t:.1f} s")

def describe(d):
    return {k: d.get(k) for k in ("dependentcomponenttype", "dependentcomponentobjectid", "dependentcomponentparentid", "requiredcomponenttype", "requiredcomponentobjectid", "dependencytype")}

if STAGE == "dependencies":
    v = default_view()
    hand = dv("POST", "savedqueries", {"name": "Spike hand view", "returnedtypecode": TABLE, "querytype": 0,
        "fetchxml": f'<fetch version="1.0" mapping="logical"><entity name="{TABLE}"><attribute name="{TABLE}id" /><attribute name="ext_name" /><attribute name="ext_sku" /></entity></fetch>',
        "layoutxml": v["layoutxml"].split("<row ")[0] + f'<row name="result" id="{TABLE}id"><cell name="ext_name" width="150" /><cell name="ext_sku" width="150" /></row></grid>'})
    say("hand view", hand["savedqueryid"])
    for c in ("ext_sku", "ext_onhand"):
        deps = dependencies(2, attribute_id(c))
        say(c, "dependents:", json.dumps([describe(d) for d in deps]))
    say("generated form", main_form()["formid"], "default view", v["savedqueryid"])
    # take ext_onhand off the generated form and view, then delete it
    layout([c for c in COLUMNS if c != "ext_onhand"])
    say("ext_onhand dependents after layout:", json.dumps([describe(d) for d in dependencies(2, attribute_id("ext_onhand"))]))
    t = time.time()
    dv("DELETE", f"EntityDefinitions(LogicalName='{TABLE}')/Attributes(LogicalName='ext_onhand')", solution=None)
    say(f"ext_onhand deleted in {time.time() - t:.1f} s")
    try:
        dv("DELETE", f"EntityDefinitions(LogicalName='{TABLE}')/Attributes(LogicalName='ext_sku')", solution=None)
        say("ext_sku deleted (unexpected)")
    except DvError as e:
        say("ext_sku delete refused:", e)

def solution_components():
    sid = rows(f"solutions?$select=solutionid&$filter=uniquename eq '{SOLUTION}'")[0]["solutionid"]
    return rows(f"solutioncomponents?$select=componenttype,objectid&$filter=_solutionid_value eq {sid}")

if STAGE == "role":
    bu = rows("businessunits?$select=businessunitid&$filter=_parentbusinessunitid_value eq null")[0]["businessunitid"]
    role = next(iter(rows(f"roles?$select=roleid&$filter=name eq 'SpikeAPI user' and _businessunitid_value eq {bu}")), None) \
        or dv("POST", "roles", {"name": "SpikeAPI user", "businessunitid@odata.bind": f"/businessunits({bu})"})
    say("role", role["roleid"])
    names = [f"prv{p}{TABLE}" for p in ("Read", "Create", "Write", "Delete", "Append", "AppendTo", "Assign", "Share")]
    found = {p["name"]: p["privilegeid"] for p in rows("privileges?$select=name,privilegeid&$filter=" + " or ".join(f"name eq '{n}'" for n in names))}
    say("privileges that exist:", sorted(found))
    dv("POST", f"roles({role['roleid']})/Microsoft.Dynamics.CRM.AddPrivilegesRole",
       {"Privileges": [{"PrivilegeId": found[f"prv{p}{TABLE}"], "PrivilegeName": f"prv{p}{TABLE}", "Depth": "Global", "BusinessUnitId": bu} for p in ("Read", "Write")]}, solution=None)
    held = rows(f"roleprivilegescollection?$select=privilegeid,privilegedepthmask&$filter=roleid eq {role['roleid']}")
    by_id = {v: k for k, v in found.items()}
    say("role holds:", sorted((by_id.get(h["privilegeid"], h["privilegeid"]), h["privilegedepthmask"]) for h in held))
    # replace the set: Read and Delete only (ReplacePrivilegesRole sets the exact list)
    dv("POST", f"roles({role['roleid']})/Microsoft.Dynamics.CRM.ReplacePrivilegesRole",
       {"Privileges": [{"PrivilegeId": found[f"prv{p}{TABLE}"], "PrivilegeName": f"prv{p}{TABLE}", "Depth": "Global", "BusinessUnitId": bu} for p in ("Read", "Delete")]}, solution=None)
    held = rows(f"roleprivilegescollection?$select=privilegeid,privilegedepthmask&$filter=roleid eq {role['roleid']}")
    say("after ReplacePrivilegesRole Read+Delete:", sorted((by_id.get(h["privilegeid"], h["privilegeid"]), h["privilegedepthmask"]) for h in held))
    say("solution components (type, id):", sorted((c["componenttype"], c["objectid"]) for c in solution_components()))

if STAGE == "api":
    role = rows("roles?$select=roleid&$filter=name eq 'SpikeAPI user'")[0]["roleid"]
    priv = rows(f"privileges?$select=privilegeid&$filter=name eq 'prvDelete{TABLE}'")[0]["privilegeid"]
    dv("POST", f"roles({role})/Microsoft.Dynamics.CRM.RemovePrivilegeRole", {"Privilege": {"@odata.type": "Microsoft.Dynamics.CRM.privilege", "privilegeid": priv}}, solution=None)
    held = [h["privilegeid"] for h in rows(f"roleprivilegescollection?$select=privilegeid&$filter=roleid eq {role}")]
    say("prvDelete removed:", priv not in held, "count now", len(held))
    api = rows("customapis?$select=customapiid,uniquename&$filter=uniquename eq 'ext_SpikeAdjust'")[0]
    params = rows(f"customapirequestparameters?$select=customapirequestparameterid&$filter=_customapiid_value eq {api['customapiid']}")
    say("api", api["customapiid"], "params", len(params))
    try:
        say("api dependents:", json.dumps([describe(d) for d in dependencies(10038, api["customapiid"])]))
    except DvError as e:
        say("api dependency check:", e)
    t = time.time()
    dv("DELETE", f"customapis({api['customapiid']})", solution=None)
    say(f"api deleted in {time.time() - t:.1f} s; params left:", len(rows(f"customapirequestparameters?$select=customapirequestparameterid&$filter=_customapiid_value eq {api['customapiid']}")))

if STAGE == "cleanup":
    for v in rows(f"savedqueries?$select=savedqueryid&$filter=name eq 'Spike hand view'"):
        dv("DELETE", f"savedqueries({v['savedqueryid']})", solution=None)
    for r in rows("roles?$select=roleid&$filter=name eq 'SpikeAPI user'"):
        dv("DELETE", f"roles({r['roleid']})", solution=None)
    for a in rows("customapis?$select=customapiid&$filter=uniquename eq 'ext_SpikeAdjust'"):
        dv("DELETE", f"customapis({a['customapiid']})", solution=None)
    t = time.time()
    try:
        dv("DELETE", f"EntityDefinitions(LogicalName='{TABLE}')", solution=None)
    except DvError as e:
        say("table delete:", e)
    say(f"table delete {time.time() - t:.1f} s")
    for d in rows("bf_brokerdatasources?$select=bf_brokerdatasourceid&$filter=bf_serviceinstance eq 'SpikeAPI'"):
        dv("DELETE", f"bf_brokerdatasources({d['bf_brokerdatasourceid']})", solution=None)
    for s in rows(f"solutions?$select=solutionid&$filter=uniquename eq '{SOLUTION}'"):
        dv("DELETE", f"solutions({s['solutionid']})", solution=None)
    say("left: table", bool(rows(f"EntityDefinitions?$select=LogicalName&$filter=LogicalName eq '{TABLE}'")),
        "role", len(rows("roles?$select=roleid&$filter=name eq 'SpikeAPI user'")),
        "solution", len(rows(f"solutions?$select=solutionid&$filter=uniquename eq '{SOLUTION}'")))
