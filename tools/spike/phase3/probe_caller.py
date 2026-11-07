"""Phase 3 spike (Task 1): what caller identity and async flag reach the host from the Custom API plug-in.

Creates a temporary unbound Custom API bf_SpikeAsync that allows async steps, registers
BrokerForge.Plugin.CustomApiPlugin on it (async first, then sync), calls it as the az CLI user with and
without CallerObjectId impersonation, prints the host's execute log lines (userObjectId, async), then
deletes everything it created. The host refuses the unknown API name; the log scope is written first.
Run with /usr/bin/python3; needs ~/.brokerforge/dev-ids.env (RG) and the az CLI signed in.
"""
import json, os, subprocess, sys, time, urllib.parse, urllib.request, urllib.error

ORG = os.environ.get("DV_ORG") or next((l.split("=", 1)[1].strip().strip('"') for l in open(os.path.expanduser("~/.brokerforge/spike-ids.env"))
                                   if l.startswith("DV_ORG=")), None) or sys.exit("Set DV_ORG, or add DV_ORG=<org url> to ~/.brokerforge/spike-ids.env")
API = ORG + "/api/data/v9.2/"
ids = dict(l.strip().split("=", 1) for l in open(os.path.expanduser("~/.brokerforge/dev-ids.env")) if "=" in l and not l.startswith("#"))
RG = ids["RG"].strip('"')
TOKEN = subprocess.check_output(["az", "account", "get-access-token", "--resource", ORG, "--query", "accessToken", "-o", "tsv"], text=True).strip()


def call(method, path, body=None, headers=None):
    req = urllib.request.Request(API + urllib.parse.quote(path, safe="/?$=&(),'@:"), method=method, data=None if body is None else json.dumps(body).encode())
    req.add_header("Authorization", "Bearer " + TOKEN)
    req.add_header("Accept", "application/json")
    req.add_header("Content-Type", "application/json")
    req.add_header("OData-Version", "4.0")
    for k, v in (headers or {}).items():
        req.add_header(k, v)
    try:
        with urllib.request.urlopen(req) as r:
            text = r.read().decode()
            return r.status, (json.loads(text) if text else None), r.headers
    except urllib.error.HTTPError as e:
        text = e.read().decode()
        return e.code, (json.loads(text) if text else None), e.headers


def one(path):
    status, body, _ = call("GET", path)
    assert status == 200, (status, body)
    return body["value"][0] if body["value"] else None


def host_lines():
    out = subprocess.run(["az", "containerapp", "logs", "show", "-n", "bf-dev-host", "-g", RG, "--type", "console", "--tail", "300", "--format", "text"],
                         capture_output=True, text=True).stdout
    return [l for l in out.splitlines() if "bf_SpikeAsync" in l]


created = {"steps": [], "api": None}
try:
    plugin = one("plugintypes?$select=plugintypeid&$filter=typename eq 'BrokerForge.Plugin.CustomApiPlugin'")["plugintypeid"]
    me = call("GET", "WhoAmI")[1]["UserId"]
    me_oid = one(f"systemusers?$select=azureactivedirectoryobjectid&$filter=systemuserid eq {me}")["azureactivedirectoryobjectid"]
    other = one(f"systemusers?$select=fullname,azureactivedirectoryobjectid&$filter=isdisabled eq false and accessmode eq 0 and azureactivedirectoryobjectid ne null and systemuserid ne {me}")
    print("caller (az CLI user) oid:", me_oid)
    print("impersonation target:", other and (other["fullname"], other["azureactivedirectoryobjectid"]))

    status, body, h = call("POST", "customapis", {
        "uniquename": "bf_SpikeAsync", "name": "bf_SpikeAsync", "displayname": "BrokerForge spike async", "description": "Phase 3 spike, temporary",
        "bindingtype": 0, "isfunction": False, "isprivate": False, "allowedcustomprocessingsteptype": 2})
    assert status in (201, 204), (status, body)
    created["api"] = one("customapis?$select=customapiid&$filter=uniquename eq 'bf_SpikeAsync'")["customapiid"]
    message = one("sdkmessages?$select=sdkmessageid&$filter=name eq 'bf_SpikeAsync'")["sdkmessageid"]

    def step(mode):
        status, body, h = call("POST", "sdkmessageprocessingsteps", {
            "name": f"bf_SpikeAsync mode {mode}", "mode": mode, "stage": 40, "rank": 1, "supporteddeployment": 0, "asyncautodelete": False,
            "plugintypeid@odata.bind": f"/plugintypes({plugin})", "sdkmessageid@odata.bind": f"/sdkmessages({message})"})
        assert status in (201, 204), (status, body)
        sid = h["OData-EntityId"].rsplit("(", 1)[1].rstrip(")")
        created["steps"].append(sid)
        return sid

    def invoke(label, impersonate):
        headers = {"CallerObjectId": other["azureactivedirectoryobjectid"]} if impersonate and other else {}
        status, body, _ = call("POST", "bf_SpikeAsync", {}, headers)
        print(f"{label}: HTTP {status}", (body or {}).get("error", {}).get("message", "")[:160])

    step(1)
    invoke("async step, plain call", False)
    if other:
        invoke("async step, CallerObjectId call", True)
    print("waiting 60 s for the async service")
    time.sleep(60)
    sync = step(0)
    invoke("sync step, plain call", False)
    if other:
        invoke("sync step, CallerObjectId call", True)
    time.sleep(10)
    print("host lines:")
    for line in host_lines():
        print(" ", line[:400])
finally:
    for sid in created["steps"]:
        print("delete step", call("DELETE", f"sdkmessageprocessingsteps({sid})")[0])
    if created["api"]:
        print("delete api", call("DELETE", f"customapis({created['api']})")[0])
