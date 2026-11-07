#!/usr/bin/env bash
# Portal connections, task 1: which solution part can carry a connection design?
# Throwaway probe, kept for the record.
#
#   tools/spike/carrier.sh            run every check, then delete what it made
#   KEEP=1 tools/spike/carrier.sh     leave the scratch solutions and components in place
#
# Writes to the dev environment named by DV_ORG (from ~/.brokerforge/spike-ids.env, through tools/spike/dv.sh), as the
# signed-in az CLI user. Creates solution BrokerForgeCarrierSpike (publisher brokerforgeext)
# and, for the import check, BrokerForgeCarrierSpike2. Prints no org URL and no token.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
# shellcheck source=dv.sh
source "$here/dv.sh"
export DV_ORG REPO="$repo" KEEP="${KEEP:-0}"
work="$(mktemp -d)"
export WORK="$work"
trap 'rm -rf "$work"' EXIT

/usr/bin/python3 -I - <<'PY'
import base64, glob, io, json, os, re, subprocess, sys, time, urllib.error, urllib.request, uuid, zipfile

ORG = os.environ["DV_ORG"].rstrip("/")
REPO = os.environ["REPO"]
WORK = os.environ["WORK"]
KEEP = os.environ.get("KEEP") == "1"
API = ORG + "/api/data/v9.2/"
SOLUTION = "BrokerForgeCarrierSpike"
SOLUTION2 = "BrokerForgeCarrierSpike2"
PUBLISHER = "brokerforgeext"

def say(*a):
    msg = " ".join(str(x) for x in a).replace(ORG, "https://contoso.crm.dynamics.com")
    print(msg, flush=True)

_tok = {"v": None, "t": 0}
def token():
    if not _tok["v"] or time.time() - _tok["t"] > 1800:
        _tok["v"] = subprocess.check_output(
            ["az", "account", "get-access-token", "--resource", ORG, "--query", "accessToken", "-o", "tsv"],
            text=True).strip()
        _tok["t"] = time.time()
    return _tok["v"]

class DvError(Exception):
    pass

def dv(method, resource, body=None, solution=SOLUTION, timeout=600):
    data = None if body is None else json.dumps(body).encode("utf-8")
    url = API + urllib.parse.quote(resource, safe="/?&=$(),'@.:;_-*")
    req = urllib.request.Request(url, data=data, method=method)
    for k, v in {"Authorization": "Bearer " + token(), "Accept": "application/json",
                 "Content-Type": "application/json; charset=utf-8", "OData-Version": "4.0",
                 "OData-MaxVersion": "4.0", "Prefer": "return=representation"}.items():
        req.add_header(k, v)
    if solution:
        req.add_header("MSCRM.SolutionUniqueName", solution)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            raw = r.read()
            return json.loads(raw) if raw else {}
    except urllib.error.HTTPError as e:
        text = e.read().decode("utf-8", "replace")
        try:
            text = json.loads(text)["error"]["message"]
        except Exception:
            pass
        raise DvError(f"HTTP {e.code}: {text[:400]}")


# ---- Step 1: a design of realistic size --------------------------------------------------
def read(p):
    with open(os.path.join(REPO, p), encoding="utf-8") as f:
        return f.read()

descriptors = {os.path.basename(p): read(os.path.relpath(p, REPO))
               for p in sorted(glob.glob(os.path.join(REPO, "descriptors/OrdersAPI/*.yaml")))}
design = {
    "formatVersion": 1,
    "instance": "OrdersAPI",
    "openapi": read("tools/mock-api/orders-openapi.yaml"),
    "answers": {"prefix": "ext", "table": "ext_order", "auth": {"mode": "Static", "header": "x-api-key"},
                "operations": {k[:-5]: {"include": True} for k in descriptors}},
    "descriptors": descriptors,
    "schema": read("schemas/OrdersAPI.yaml"),
}
def dumps(d):
    return json.dumps(d, ensure_ascii=False, indent=2)
small = dumps(design)
n1 = len(small.encode("utf-8"))
padded = dict(design)
padded["padding"] = ""
while len(dumps(padded).encode("utf-8")) < 4 * n1:
    padded["padding"] += small[: 4 * n1 - len(dumps(padded).encode("utf-8"))]
large = dumps(padded)
say(f"design: {n1} bytes ({len(small)} chars); padded copy: {len(large.encode('utf-8'))} bytes")
SIZES = {"1x": small, "4x": large}

# ---- scratch solution ------------------------------------------------------------------
created = []  # (entityset, id) to delete, newest last

def ensure_solution(name):
    got = dv("GET", f"solutions?$filter=uniquename eq '{name}'&$select=solutionid", solution=None)["value"]
    if got:
        return got[0]["solutionid"]
    pub = dv("GET", f"publishers?$filter=uniquename eq '{PUBLISHER}'&$select=publisherid", solution=None)["value"][0]
    s = dv("POST", "solutions", {"uniquename": name, "friendlyname": name, "version": "1.0.0.0",
                                 "publisherid@odata.bind": f"/publishers({pub['publisherid']})"}, solution=None)
    return s["solutionid"]

def publish_all():
    # PublishXml for the spike's web resources only. PublishAllXml once hung past 600 s here.
    ids = [i for es, i in created if es == "webresourceset"]
    if ids:
        xml = "<importexportxml><webresources>" + "".join(f"<webresource>{{{i}}}</webresource>" for i in ids) \
              + "</webresources></importexportxml>"
        dv("POST", "PublishXml", {"ParameterXml": xml}, solution=None)

def export(name):
    r = dv("POST", "ExportSolution", {"SolutionName": name, "Managed": False}, solution=None)
    return base64.b64decode(r["ExportSolutionFile"])

def import_zip(blob):
    # ImportSolutionAsync, then poll the system job. A synchronous ImportSolution call once hung
    # past 600 s although its import job had completed in 5 s.
    job = str(uuid.uuid4())
    r = dv("POST", "ImportSolutionAsync", {"CustomizationFile": base64.b64encode(blob).decode("ascii"),
                                           "OverwriteUnmanagedCustomizations": True, "PublishWorkflows": False,
                                           "ImportJobId": job}, solution=None)
    op = r["AsyncOperationId"]
    for _ in range(120):
        a = dv("GET", f"asyncoperations({op})?$select=statecode,statuscode,message", solution=None)
        if a["statecode"] == 3:  # completed
            if a["statuscode"] != 30:
                ij = dv("GET", f"importjobs({job})?$select=progress", solution=None)
                raise DvError(f"import failed: status {a['statuscode']} {a.get('message')} progress {ij.get('progress')}")
            return
        time.sleep(5)
    raise DvError("import did not finish in 10 minutes")

def zip_text_hits(blob, needle):
    """Entries in the zip whose raw bytes contain needle (the JSON, UTF-8) as plain text."""
    hits = []
    with zipfile.ZipFile(io.BytesIO(blob)) as z:
        for n in z.namelist():
            if needle.encode("utf-8") in z.read(n):
                hits.append(n)
        names = z.namelist()
    return hits, names

def rename_solution(blob, new):
    out = io.BytesIO()
    with zipfile.ZipFile(io.BytesIO(blob)) as zin, zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as zout:
        for item in zin.infolist():
            data = zin.read(item.filename)
            if item.filename == "solution.xml":
                s = data.decode("utf-8")
                s = s.replace(f"<UniqueName>{SOLUTION}</UniqueName>", f"<UniqueName>{new}</UniqueName>")
                s = re.sub(r'(<LocalizedName description=")[^"]*(")', r"\g<1>" + new + r"\2", s, count=1)
                data = s.encode("utf-8")
            zout.writestr(item, data)
    return out.getvalue()

results = []  # rows for the findings table
def row(candidate, check, outcome, note=""):
    results.append((candidate, check, outcome, note))
    say(f"  [{outcome}] {candidate} / {check} {('- ' + note) if note else ''}")

# ---- Candidate 1: environment variable definition, type JSON ---------------------------
def candidate_env_json():
    c = "env var JSON"
    say("candidate 1: environment variable definition, type JSON (100000003)")
    ok = True
    for label, text in SIZES.items():
        schema = f"ext_CarrierSpikeDesign{label}"
        try:
            r = dv("POST", "environmentvariabledefinitions",
                   {"schemaname": schema, "displayname": schema, "type": 100000003, "defaultvalue": text})
            created.append(("environmentvariabledefinitions", r["environmentvariabledefinitionid"]))
            back = dv("GET", f"environmentvariabledefinitions({r['environmentvariabledefinitionid']})?$select=defaultvalue")["defaultvalue"]
            row(c, f"save + read back {label} ({len(text.encode('utf-8'))} B)", "pass" if back == text else "FAIL",
                "" if back == text else "read back differs")
            ok = ok and back == text
        except DvError as e:
            row(c, f"save {label} ({len(text.encode('utf-8'))} B)", "FAIL", str(e))
            ok = False
    # measured limit on defaultvalue: largest JSON string that saves
    for n in (2000, 2001):
        text = json.dumps({"p": "x" * (n - 9)})
        assert len(text) == n
        try:
            r = dv("POST", "environmentvariabledefinitions",
                   {"schemaname": f"ext_CarrierSpikeLimit{n}", "displayname": f"limit {n}", "type": 100000003,
                    "defaultvalue": text})
            created.append(("environmentvariabledefinitions", r["environmentvariabledefinitionid"]))
            row(c, f"limit probe {n} chars", "saves")
        except DvError as e:
            row(c, f"limit probe {n} chars", "refused", str(e))
    return ok

# ---- Candidate 2: web resource, XML (type 4) holding the JSON in CDATA ------------------
WR_NAME = "ext_/brokerforge/carrierspike/design{}.xml"

def wrap(text):
    if "]]>" in text:
        raise ValueError("design contains ]]>, cannot sit in one CDATA section")
    return '<?xml version="1.0" encoding="utf-8"?>\n<design format="json"><![CDATA[' + text + "]]></design>\n"

def unwrap(xml):
    m = re.fullmatch(r'<\?xml version="1.0" encoding="utf-8"\?>\n<design format="json"><!\[CDATA\[(.*)\]\]></design>\n',
                     xml, re.S)
    if not m:
        raise ValueError("web resource is not a design wrapper")
    return m.group(1)

def wr_get(name):
    v = dv("GET", f"webresourceset?$filter=name eq '{name}'&$select=webresourceid,content,webresourcetype")["value"]
    return v

def wr_read(name):
    v = wr_get(name)
    if len(v) != 1:
        raise ValueError(f"{len(v)} web resources named {name}")
    return unwrap(base64.b64decode(v[0]["content"]).decode("utf-8")), v[0]

def candidate_webresource():
    c = "web resource XML"
    say("candidate 2: web resource, type 4 (XML), JSON in CDATA")
    ok = True
    ids = {}
    for label, text in SIZES.items():
        name = WR_NAME.format(label)
        try:
            body = base64.b64encode(wrap(text).encode("utf-8")).decode("ascii")
            r = dv("POST", "webresourceset", {"name": name, "displayname": name, "webresourcetype": 4, "content": body})
            ids[label] = r["webresourceid"]
            created.append(("webresourceset", r["webresourceid"]))
        except DvError as e:
            row(c, f"save {label}", "FAIL", str(e)); ok = False
    if not ok:
        return False
    publish_all()
    for label, text in SIZES.items():
        back, _ = wr_read(WR_NAME.format(label))
        same = back.encode("utf-8") == text.encode("utf-8")
        row(c, f"save + read back {label} ({len(text.encode('utf-8'))} B)", "pass" if same else "FAIL")
        ok = ok and same

    # export: the JSON must be visible as text in the zip
    blob = export(SOLUTION)
    open(os.path.join(WORK, "export.zip"), "wb").write(blob)
    for label, text in SIZES.items():
        hits, names = zip_text_hits(blob, text)
        wr_files = [n for n in names if n.lower().startswith("webresources/")]
        row(c, f"export {label}: JSON is plain text in zip", "pass" if hits else "FAIL",
            "in " + ", ".join(hits) if hits else "web resource files: " + ", ".join(wr_files))
        ok = ok and bool(hits)

    # re-import the same zip: value unchanged
    import_zip(blob)
    for label, text in SIZES.items():
        back, _ = wr_read(WR_NAME.format(label))
        same = back == text
        row(c, f"re-import same solution {label}: unchanged", "pass" if same else "FAIL"); ok = ok and same

    # change the value in the environment, then import the zip under a new solution name:
    # the import must put the exported value back, byte for byte
    for label in SIZES:
        dv("PATCH", f"webresourceset({ids[label]})",
           {"content": base64.b64encode(wrap('{"changed":true}').encode("utf-8")).decode("ascii")})
    publish_all()
    changed, _ = wr_read(WR_NAME.format("1x"))
    assert changed == '{"changed":true}', "edit did not land"
    import_zip(rename_solution(blob, SOLUTION2))
    publish_all()
    for label, text in SIZES.items():
        back, _ = wr_read(WR_NAME.format(label))
        same = back == text
        row(c, f"edit, then import as {SOLUTION2} {label}: exported value restored", "pass" if same else "FAIL")
        ok = ok and same

    # measured limit: content is base64 and capped by organization.maxuploadfilesize
    limit = dv("GET", "organizations?$select=maxuploadfilesize", solution=None)["value"][0]["maxuploadfilesize"]
    say(f"  organization.maxuploadfilesize = {limit} bytes")
    for raw in (limit - 4096, limit + 4096):
        text = json.dumps({"p": "x" * (raw - 200)})
        xml = wrap(text)
        name = f"ext_/brokerforge/carrierspike/limit{raw}.xml"
        try:
            r = dv("POST", "webresourceset", {"name": name, "displayname": name, "webresourcetype": 4,
                                              "content": base64.b64encode(xml.encode("utf-8")).decode("ascii")})
            created.append(("webresourceset", r["webresourceid"]))
            row(c, f"limit probe {len(xml)} B file ({len(base64.b64encode(xml.encode()))} B base64)", "saves")
        except DvError as e:
            row(c, f"limit probe {len(xml)} B file ({len(base64.b64encode(xml.encode()))} B base64)", "refused", str(e))
    return ok

# ---- Step 3: environment variable override and a 300-character String value ------------
def env_override():
    c = "env var String"
    say("step 3: String environment variable, default overridden by a current value")
    url = "https://api.contoso.com/" + "a" * (300 - len("https://api.contoso.com/") - 1) + "/"
    assert len(url) == 300
    d = dv("POST", "environmentvariabledefinitions",
           {"schemaname": "ext_CarrierSpikeBaseUrl", "displayname": "CarrierSpike BaseUrl", "type": 100000000,
            "defaultvalue": url})
    did = d["environmentvariabledefinitionid"]
    created.append(("environmentvariabledefinitions", did))
    back = dv("GET", f"environmentvariabledefinitions({did})?$select=defaultvalue")["defaultvalue"]
    row(c, "300-character URL as default, read back", "pass" if back == url else "FAIL")
    def effective():
        r = dv("GET", "environmentvariabledefinitions?$filter=schemaname eq 'ext_CarrierSpikeBaseUrl'"
                      "&$select=defaultvalue&$expand=environmentvariabledefinition_environmentvariablevalue($select=value)")
        d0 = r["value"][0]
        vals = d0["environmentvariabledefinition_environmentvariablevalue"]
        return vals[0]["value"] if vals else d0["defaultvalue"]
    row(c, "no current value: effective = default", "pass" if effective() == url else "FAIL")
    over = "https://test.contoso.com/v2"
    v = dv("POST", "environmentvariablevalues",
           {"schemaname": "ext_CarrierSpikeBaseUrl", "value": over,
            "EnvironmentVariableDefinitionId@odata.bind": f"/environmentvariabledefinitions({did})"})
    created.append(("environmentvariablevalues", v["environmentvariablevalueid"]))
    row(c, "current value set: effective = current value", "pass" if effective() == over else "FAIL")
    # the platform's own resolver, if exposed
    try:
        r = dv("GET", "RetrieveEnvironmentVariableValue(DefinitionSchemaName='ext_CarrierSpikeBaseUrl')", solution=None)
        row(c, "RetrieveEnvironmentVariableValue returns the current value", "pass" if r.get("Value") == over else "FAIL",
            f"returned keys {sorted(k for k in r if not k.startswith('@'))}")
    except DvError as e:
        row(c, "RetrieveEnvironmentVariableValue", "n/a", str(e))
    # export: does the current value travel with the solution?
    blob = export(SOLUTION)
    with zipfile.ZipFile(io.BytesIO(blob)) as z:
        files = [n for n in z.namelist() if "CarrierSpikeBaseUrl" in n]
        has_over = any(over.encode() in z.read(n) for n in z.namelist())
        has_def = any(url.encode() in z.read(n) for n in z.namelist())
    row(c, "export carries the default as text", "pass" if has_def else "FAIL", ", ".join(files))
    row(c, "export carries the current value", "yes" if has_over else "no",
        "current value is in the solution" if has_over else "current value stays in the environment")

def cleanup():
    say("cleanup")
    for es, i in reversed(created):
        try:
            dv("DELETE", f"{es}({i})", solution=None)
        except DvError as e:
            if "404" not in str(e) and "Does Not Exist" not in str(e):
                say(f"  could not delete {es}({i}): {e}")
    # anything left by name (imports can create rows this run did not record)
    for name in ("ext_/brokerforge/carrierspike/",):
        for w in dv("GET", f"webresourceset?$filter=startswith(name,'{name}')&$select=webresourceid", solution=None)["value"]:
            dv("DELETE", f"webresourceset({w['webresourceid']})", solution=None)
    for d in dv("GET", "environmentvariabledefinitions?$filter=startswith(schemaname,'ext_CarrierSpike')"
                       "&$select=environmentvariabledefinitionid", solution=None)["value"]:
        dv("DELETE", f"environmentvariabledefinitions({d['environmentvariabledefinitionid']})", solution=None)
    for s in (SOLUTION2, SOLUTION):
        for x in dv("GET", f"solutions?$filter=uniquename eq '{s}'&$select=solutionid", solution=None)["value"]:
            dv("DELETE", f"solutions({x['solutionid']})", solution=None)
    left = (dv("GET", "webresourceset?$filter=startswith(name,'ext_/brokerforge/carrierspike/')&$select=name", solution=None)["value"]
            + dv("GET", "environmentvariabledefinitions?$filter=startswith(schemaname,'ext_CarrierSpike')&$select=schemaname", solution=None)["value"]
            + dv("GET", "solutions?$filter=startswith(uniquename,'BrokerForgeCarrierSpike')&$select=uniquename", solution=None)["value"])
    say("  left behind:", len(left))

chosen = None
try:
    ensure_solution(SOLUTION)
    for name, fn in (("EnvironmentVariableJson", candidate_env_json), ("WebResource", candidate_webresource)):
        if fn():
            chosen = name
            break
    if chosen is None:
        say("candidates 1 and 2 failed: probe candidate 3 (bf_brokerdatasource column) before ruling")
    env_override()
finally:
    if not KEEP:
        cleanup()
say(f"Carrier = {chosen or 'none yet'}")
PY
