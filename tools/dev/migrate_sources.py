"""Move today's sources onto connections (plan 5, spec 10): OrdersAPI, TicketsAPI and TicketsEntraAPI.

For each instance named on the command line it creates or updates the bf_connection row with the values the Bicep
settings had (base URL, authentication, prefix, the source's existing solution), the definition next to the mock in
tools/mock-api and the answers in tests/SwaggerForge.Tests/fixtures. It then calls bf_ReadDefinition, the Custom API
behind the review page, and prints the status, the change list and what is pending. With --generate it calls
bf_Generate with the fingerprint of that list and waits for the job on the row, as the Generate button does.

It refuses to generate when the list holds anything but additions, or anything refused or blocked: today's tables
must carry over as they are. That exits 4 with the list printed. Keys are not set here (tools/dev/set-instance-secret.sh).

Runs as the az CLI user against DV_ORG. Usage: migrate_sources.py [--generate] <Instance>...
"""
import json, os, subprocess, sys, time

sys.path.insert(0, os.path.dirname(__file__))
from acceptance import API, call, error_message  # noqa: E402

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
FIXTURES = os.path.join(ROOT, "tests", "SwaggerForge.Tests", "fixtures")
MOCKS = os.path.join(ROOT, "tools", "mock-api")
MODES = {"ServiceAccount": 100010000, "Static": 100010001, "OAuth": 100010002, "Impersonate": 100010003, "Sso": 100010004}


def ids(path):
    values = {}
    for line in open(os.path.expanduser(path)):
        if "=" in line and not line.lstrip().startswith("#"):
            k, v = line.split("=", 1)
            values[k.strip()] = v.strip().strip('"')
    return values


def fqdn(app):
    """https://<ingress name> of a container app in the dev resource group."""
    rg = ids("~/.brokerforge/dev-ids.env")["RG"]
    name = subprocess.check_output(["az", "containerapp", "show", "-g", rg, "-n", app, "--query", "properties.configuration.ingress.fqdn", "-o", "tsv"], text=True).strip()
    return "https://" + name


def sources():
    mock, tickets = fqdn("bf-dev-orders-mock"), fqdn("bf-dev-tickets-mock")
    api_app = ids("~/.brokerforge/dev-ids.env")["TICKETS_API_APP"]
    return {
        "OrdersAPI": dict(definition="orders-openapi.yaml", answers="orders-answers.json", bf_baseurl=mock, bf_authmode=MODES["Static"],
                          bf_statickind="apiKeyHeader", bf_headername="X-Api-Key", bf_solution="BrokerForgeOrders"),
        "TicketsAPI": dict(definition="tickets-openapi.yaml", answers="tickets-answers.json", bf_baseurl=tickets, bf_authmode=MODES["OAuth"],
                           bf_authorizeurl=tickets + "/tk/oauth/authorize", bf_tokenurl=tickets + "/tk/oauth/token", bf_oauthscopes="tickets.read",
                           bf_clientid="bf-tickets", bf_usesconnectapp=False, bf_solution="BrokerForgeTickets"),
        "TicketsEntraAPI": dict(definition="tickets-openapi.yaml", answers="tickets-entra-answers.json", bf_baseurl=tickets, bf_authmode=MODES["Impersonate"],
                                bf_oauthscopes=f"api://{api_app}/user_impersonation offline_access", bf_usesconnectapp=True,
                                bf_solution="BrokerForgeTicketsEntra"),
    }


def die(code, text):
    print(text, flush=True)
    sys.exit(code)


def ensure_row(name, spec):
    row = {k: v for k, v in spec.items() if k.startswith("bf_")}
    row.update(bf_name=name, bf_prefix="ext", bf_openapi=open(os.path.join(MOCKS, spec["definition"])).read(),
               bf_answers=json.dumps(json.load(open(os.path.join(FIXTURES, spec["answers"]))), indent=2))
    status, body, _ = call("GET", f"bf_connections?$select=bf_connectionid&$filter=bf_name eq '{name}'")
    found = body.get("value", [])
    if len(found) > 1:
        die(1, f"{name}: {len(found)} connection rows have this name; remove the extra ones first")
    if found:
        cid = found[0]["bf_connectionid"]
        status, body, _ = call("PATCH", f"bf_connections({cid})", row)
    else:
        status, body, h = call("POST", "bf_connections", row)
        cid = (h.get("OData-EntityId") or h.get("odata-entityid") or "").rsplit("(", 1)[-1].rstrip(")")
    if status not in (200, 204) or not cid:
        die(1, f"{name}: writing the row failed with HTTP {status}: {error_message(body)}")
    return cid


def read(name, cid):
    status, body, _ = call("POST", "bf_ReadDefinition", {"ConnectionId": cid})
    if status != 200:
        die(1, f"{name}: bf_ReadDefinition failed with HTTP {status}: {error_message(body)}")
    review = json.loads(body["Review"])
    preview = review.get("preview") or {}
    print(f"{name}: {review['status']}", flush=True)
    for e in review["review"]["endpoints"]:
        open_q = [q["key"] for q in e["questions"] if q.get("open")]
        print(f"  {e['operation']:<14} {e['becomes']:<10} {e['status']}{' open: ' + ', '.join(open_q) if open_q else ''}", flush=True)
    for title in ("changes", "refused", "blocked", "parts", "pending"):
        for line in preview.get(title) or []:
            print(f"  {title}: {line}", flush=True)
    if preview.get("error"):
        print(f"  preview error: {preview['error']}", flush=True)
    keys = review["environment"]["secrets"]
    print(f"  keys: {', '.join(k['name'] + (' set' if k['set'] else ' needs key') for k in keys) or 'none'}", flush=True)
    return review, preview


def generate(name, cid, preview):
    if preview.get("error") or preview.get("refused") or preview.get("blocked"):
        die(4, f"{name}: not generated; the preview has an error, a refusal or a block (above)")
    others = [c for c in preview.get("changes") or [] if not c.startswith("add ")]
    if others:
        die(4, f"{name}: not generated; the change list has more than additions: {others}")
    status, body, _ = call("POST", "bf_Generate", {"ConnectionId": cid, "Fingerprint": preview["fingerprint"]})
    if status != 200:
        die(1, f"{name}: bf_Generate failed with HTTP {status}: {error_message(body)}")
    job = body["JobId"]
    print(f"  job {job} {body['State']}", flush=True)
    for _ in range(180):
        time.sleep(5)
        _, row, _ = call("GET", f"bf_connections({cid})?$select=bf_generationstate,bf_generationjobid,bf_generationresult")
        if row.get("bf_generationjobid") == job and row.get("bf_generationstate") in ("Succeeded", "Failed"):
            print(f"  {row['bf_generationstate']}: {row.get('bf_generationresult')}", flush=True)
            return row["bf_generationstate"] == "Succeeded"
    die(1, f"{name}: job {job} did not finish in 15 minutes")


def main(args):
    go = "--generate" in args
    names = [a for a in args if not a.startswith("--")]
    known = sources()
    if not names or any(n not in known for n in names):
        die(2, f"usage: migrate_sources.py [--generate] <{'|'.join(known)}>...")
    print(f"Dataverse: {API}", flush=True)
    ok = True
    for name in names:
        cid = ensure_row(name, known[name])
        review, preview = read(name, cid)
        if go:
            if review["status"] != "Ready":
                die(4, f"{name}: not generated; the review is {review['status']}")
            ok = generate(name, cid, preview) and ok
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main(sys.argv[1:])
