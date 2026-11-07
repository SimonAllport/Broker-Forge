"""Inventory acceptance: the phase 2 "onboarded with no new code" source, through the Dataverse Web API as the az CLI user.

Covers list across an offset page boundary, an option set filter, retrieve, create, update, delete, the
ext_AdjustStock Custom API, an undeclared filter and a missing required Custom API input. Leaves the mock's
data as it found it (the created item is deleted and the stock adjustment is reversed).
"""
import os, sys

sys.path.insert(0, os.path.dirname(__file__))
from acceptance import call, check, correlation, error_message  # noqa: E402


def main():
    # 1. List across an offset page boundary: 45 items, 20 per page, every item exactly once.
    ids, url, pages = [], "ext_items?$select=ext_name,ext_onhand,ext_state", 0
    while url:
        status, body, h = call("GET", url, page_size=20, name="list page")
        check(1, status == 200, f"list page {pages + 1} returns HTTP {status} with {len(body.get('value', []))} rows", h)
        ids += [row["ext_itemid"] for row in body["value"]]
        url, pages = body.get("@odata.nextLink"), pages + 1
    check(1, len(ids) == 45 and len(set(ids)) == 45 and pages == 3, f"{len(ids)} items over {pages} pages, {len(set(ids))} distinct", h)

    # 2. Option set filter, reverse-mapped to the source value.
    status, body, h = call("GET", "ext_items?$select=ext_name&$filter=ext_state eq 100000001", page_size=50)
    check(2, status == 200 and len(body.get("value", [])) == 9, f"ext_state eq discontinued returns {len(body.get('value', []))} items (expected 9)", h)

    # 3. Retrieve one item by its integer key.
    status, body, h = call("GET", "ext_items?$select=ext_name&$filter=ext_name eq 'Item 007'")
    check(3, status == 200 and len(body.get("value", [])) == 1, "name filter finds Item 007", h)
    item7 = body["value"][0]["ext_itemid"]
    status, body, h = call("GET", f"ext_items({item7})?$select=ext_name,ext_sku,ext_onhand,ext_price,ext_state,ext_updatedat", name="retrieve")
    check(3, status == 200 and body.get("ext_sku") == "SKU-007" and body.get("ext_price") == 8.75 and body.get("ext_state") == 100000000,
          f"retrieve Item 007: {body.get('ext_sku')} / {body.get('ext_price')} / {body.get('ext_state')}", h)

    # 4. Create, update, delete.
    status, body, h = call("POST", "ext_items", {"ext_name": "Acceptance widget", "ext_sku": "ACC-1", "ext_onhand": 4, "ext_price": 2.5, "ext_state": 100000000}, name="create")
    entity = h.get("OData-EntityId", "") or ""
    check(4, status == 204 and "ext_items(" in entity, f"create returns 204 with {entity}", h)
    new_id = entity[entity.index("(") + 1:entity.index(")")]
    status, body, h = call("PATCH", f"ext_items({new_id})", {"ext_price": 3.75, "ext_state": 100000001}, name="update")
    check(4, status == 204, f"update returns HTTP {status}", h)
    status, body, h = call("GET", f"ext_items({new_id})?$select=ext_name,ext_price,ext_state", name="retrieve")
    check(4, body.get("ext_name") == "Acceptance widget" and body.get("ext_price") == 3.75 and body.get("ext_state") == 100000001,
          f"after update: {body.get('ext_name')} / {body.get('ext_price')} / {body.get('ext_state')}", h)
    status, body, h = call("DELETE", f"ext_items({new_id})", name="delete")
    check(4, status == 204, f"delete returns HTTP {status}", h)
    status, body, h = call("GET", f"ext_items({new_id})?$select=ext_name")
    msg = error_message(body)
    check(4, status in (400, 404) and msg.startswith("NotFound:"), f"retrieve after delete fails with HTTP {status}: {msg[:100]}", h, correlation(msg))

    # 5. The ext_AdjustStock Custom API, then reverse it.
    status, body, h = call("GET", "ext_items?$select=ext_onhand&$filter=ext_name eq 'Item 003'")
    before = body["value"][0]["ext_onhand"]
    status, body, h = call("POST", "ext_AdjustStock", {"ItemId": 3, "Delta": 5, "Reason": "acceptance"}, name="custom api")
    check(5, status == 200 and body.get("OnHand") == before + 5 and str(body.get("AdjustedAt", "")).startswith("20"),
          f"ext_AdjustStock returns OnHand {body.get('OnHand')} (was {before}), AdjustedAt {body.get('AdjustedAt')}", h)
    status, body, h = call("POST", "ext_AdjustStock", {"ItemId": 3, "Delta": -5}, name="custom api")
    check(5, status == 200 and body.get("OnHand") == before, f"reversing the adjustment returns OnHand {body.get('OnHand')}", h)
    status, body, h = call("POST", "ext_AdjustStock", {"ItemId": 3, "Delta": -1000})
    msg = error_message(body)
    check(5, status == 400 and msg.startswith("Validation:") and "on hand" in msg, f"an adjustment below zero fails with HTTP {status}: {msg[:120]}", h, correlation(msg))

    # 6. Undeclared filter fails loudly.
    status, body, h = call("GET", "ext_items?$select=ext_name&$filter=ext_sku eq 'SKU-001'")
    msg = error_message(body)
    check(6, status == 400 and "UnsupportedQueryException" in msg and "ext_sku" in msg, f"undeclared filter returns HTTP {status}: {msg[:140]}", h, correlation(msg))

    # 7. A missing required input is refused before any source call (by the platform or the host).
    status, body, h = call("POST", "ext_AdjustStock", {"ItemId": 3})
    msg = error_message(body)
    check(7, status == 400 and "Delta" in msg, f"missing Delta returns HTTP {status}: {msg[:160]}", h, correlation(msg))

    print("\nall Inventory acceptance assertions passed")


if __name__ == "__main__":
    main()
