"""Orders acceptance: the phase 1 "done when" criteria plus the phase 2 descriptor features
(8: option set writes, 9: ext_lines_json read back), through the Dataverse Web API as the az CLI user.

Exits non-zero on the first failed assertion. Each line prints the Dataverse request ID of the call;
error assertions also print the BrokerForge correlation ID from the message.
"""
import json, os, re, statistics, subprocess, sys, time, urllib.error, urllib.parse, urllib.request

ORG = os.environ.get("DV_ORG") or next((l.split("=", 1)[1].strip().strip('"') for l in open(os.path.expanduser("~/.brokerforge/spike-ids.env"))
                                   if l.startswith("DV_ORG=")), None) or sys.exit("Set DV_ORG, or add DV_ORG=<org url> to ~/.brokerforge/spike-ids.env")
API = ORG + "/api/data/v9.2/"
TOKEN = subprocess.check_output(["az", "account", "get-access-token", "--resource", ORG, "--query", "accessToken", "-o", "tsv"], text=True).strip()
TIMINGS = {}


def call(method, url, body=None, page_size=None, name=None):
    headers = {"Authorization": "Bearer " + TOKEN, "Accept": "application/json", "OData-Version": "4.0", "OData-MaxVersion": "4.0",
               "Content-Type": "application/json; charset=utf-8"}
    if page_size:
        headers["Prefer"] = f"odata.maxpagesize={page_size}"
    full = url if url.startswith("http") else API + urllib.parse.quote(url, safe="/?$=&'(),:@%")
    req = urllib.request.Request(full, data=None if body is None else json.dumps(body).encode(), method=method, headers=headers)
    start = time.perf_counter()
    try:
        with urllib.request.urlopen(req, timeout=180) as r:
            status, text, h = r.status, r.read().decode(), dict(r.headers)
    except urllib.error.HTTPError as e:
        status, text, h = e.code, e.read().decode(), dict(e.headers)
    elapsed = (time.perf_counter() - start) * 1000
    if name:
        TIMINGS.setdefault(name, []).append(elapsed)
    return status, (json.loads(text) if text.strip().startswith("{") else {}), h


def request_id(h):
    return h.get("x-ms-service-request-id") or h.get("REQ_ID") or "?"


def check(n, ok, text, h, extra=""):
    print(f"[{'PASS' if ok else 'FAIL'}] {n}. {text} (request {request_id(h)}{extra})", flush=True)
    if not ok:
        sys.exit(1)


def error_message(body):
    return (body.get("error") or {}).get("message", "")


def correlation(message):
    m = re.search(r"correlationId ([0-9a-f-]+)", message)
    return f", correlationId {m.group(1)}" if m else ""


def remove_leftovers():
    """Deletes orders an interrupted earlier run created, so the seed counts below hold."""
    for ref in ("CUST-ACCEPT", "CUST-UPDATED"):
        status, body, _ = call("GET", f"ext_orders?$select=ext_name&$filter=ext_customerref eq '{ref}'")
        for row in body.get("value", []) if status == 200 else []:
            call("DELETE", f"ext_orders({row['ext_orderid']})")
            print(f"removed leftover {row['ext_name']} ({ref})")


def main():
    remove_leftovers()

    # 1. Declared filter and cursor paging across Dataverse pages.
    url = "ext_orders?$select=ext_name,ext_status,ext_customerref&$filter=ext_status eq 100000000"
    rows, pages, first_headers = [], 0, None
    while url:
        status, body, h = call("GET", url, page_size=25, name="list page")
        first_headers = first_headers or h
        if status != 200:
            check(1, False, f"list returned HTTP {status}: {error_message(body)[:300]}", h)
        rows += body["value"]
        url = body.get("@odata.nextLink")
        pages += 1
    names = [r["ext_name"] for r in rows]
    check(1, len(rows) == 40 and len(set(names)) == 40 and all(r["ext_status"] == 100000000 for r in rows),
          f"status eq open, maxpagesize 25: {len(rows)} rows over {pages} pages, all open, no duplicates", first_headers)

    # 2. Retrieve by id.
    first = rows[0]
    status, body, h = call("GET", f"ext_orders({first['ext_orderid']})?$select=ext_name,ext_customerref,ext_total", name="retrieve")
    check(2, status == 200 and body.get("ext_name") == first["ext_name"], f"retrieve {first['ext_orderid']} returns {body.get('ext_name')}", h)
    # The mock's seed total for order n is n x 10.50; the decimal column must carry it.
    expected_total = int(first["ext_name"].split("-")[1]) * 10.5
    check(2, body.get("ext_total") == expected_total, f"retrieve returns decimal ext_total {body.get('ext_total')} (expected {expected_total})", h)

    # 3. Create, then read it back. The option set is written as the source value (phase 2, D-21).
    status, body, h = call("POST", "ext_orders", {"ext_customerref": "CUST-ACCEPT", "ext_lines_json": '[{"sku":"A","qty":2}]', "ext_total": 12.5,
                                                 "ext_status": 100000001}, name="create")
    entity_id = re.search(r"\(([0-9a-f-]{36})\)", h.get("OData-EntityId", "") or "")
    check(3, status == 204 and entity_id is not None, f"create returns 204 with OData-EntityId {h.get('OData-EntityId')}", h)
    new_id = entity_id.group(1)
    status, body, h = call("GET", f"ext_orders({new_id})?$select=ext_name,ext_customerref,ext_total,ext_status,ext_lines_json", name="retrieve")
    check(3, status == 200 and body.get("ext_customerref") == "CUST-ACCEPT" and body.get("ext_total") == 12.5,
          f"created order reads back as {body.get('ext_name')} / {body.get('ext_customerref')} / {body.get('ext_total')}", h)
    check(8, body.get("ext_status") == 100000001, f"option set written on create reads back as {body.get('ext_status')} (shipped)", h)
    check(9, body.get("ext_lines_json") == '[{"sku":"A","qty":2}]', f"ext_lines_json reads back as {body.get('ext_lines_json')}", h)

    # 4. Update a column and the option set.
    status, body, h = call("PATCH", f"ext_orders({new_id})", {"ext_customerref": "CUST-UPDATED", "ext_status": 100000002}, name="update")
    check(4, status == 204, f"update returns HTTP {status}", h)
    status, body, h = call("GET", f"ext_orders({new_id})?$select=ext_customerref,ext_status", name="retrieve")
    check(4, body.get("ext_customerref") == "CUST-UPDATED", f"after update ext_customerref is {body.get('ext_customerref')}", h)
    check(8, body.get("ext_status") == 100000002, f"option set written on update reads back as {body.get('ext_status')} (cancelled)", h)

    # 5. Delete, then the record is gone.
    status, body, h = call("DELETE", f"ext_orders({new_id})", name="delete")
    check(5, status == 204, f"delete returns HTTP {status}", h)
    # The platform returns HTTP 400 / 0x80040265 for every provider error, whatever code the plug-in raises
    # (phase 1 findings), so "not found" is asserted on the BrokerForge kind in the message.
    status, body, h = call("GET", f"ext_orders({new_id})?$select=ext_name")
    msg = error_message(body)
    check(5, status in (400, 404) and msg.startswith("NotFound:") and "correlationId" in msg,
          f"retrieve after delete fails with HTTP {status}: {msg[:120]}", h, correlation(msg))

    # 6. Undeclared filter fails loudly.
    status, body, h = call("GET", "ext_orders?$select=ext_name&$filter=ext_name eq 'REF-0001'")
    msg = error_message(body)
    check(6, status == 400 and "UnsupportedQueryException" in msg and "ext_name" in msg and "correlationId" in msg,
          f"undeclared filter returns HTTP {status}: {msg[:160]}", h, correlation(msg))

    # 7. Unmapped source 500 fails loudly with the correlation ID.
    status, body, h = call("GET", "ext_orders?$select=ext_name&$filter=ext_customerref eq '__fail500'")
    msg = error_message(body)
    check(7, status == 400 and "SourceException" in msg and "500" in msg and "correlationId" in msg,
          f"unmapped 500 returns HTTP {status}: {msg[:160]}", h, correlation(msg))

    # Warm latency sample for the findings (end to end through Dataverse).
    for _ in range(10):
        call("GET", f"ext_orders({first['ext_orderid']})?$select=ext_name", name="retrieve (warm)")
        call("GET", "ext_orders?$select=ext_name&$top=10", name="list top 10 (warm)")
    print("\nend-to-end latency through the Web API (ms):")
    for name, values in TIMINGS.items():
        values = sorted(values)
        p95 = values[min(len(values) - 1, int(round(0.95 * (len(values) - 1))))]
        print(f"  {name:22} n={len(values):2}  median {statistics.median(values):7.0f}  p95 {p95:7.0f}  max {values[-1]:7.0f}")
    print("\nall Orders acceptance assertions passed")


if __name__ == "__main__":
    main()
