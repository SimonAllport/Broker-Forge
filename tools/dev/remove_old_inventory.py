"""Remove InventoryAPI from dev (plan 5 task 6), so done criterion 1 starts with no source: what `bf generate` made
(solution BrokerForgeInventory) and what an earlier take through the screens made (solution BrokerForgeInventoryAPI).

Deletes the connection row, the Custom API ext_AdjustStock (its parameters go with it), the role InventoryAPI user,
the virtual table ext_item, its data source record, the design web resource and its two environment variables, and
both solutions. Virtual tables hold no data: the items stay in the mock. Runs only against the dev
org in ~/.brokerforge/spike-ids.env (DV_ORG), as the az CLI user, and stops at the first refusal, printing what
Dataverse said (a dependency such as an app that still shows the table, which is then removed by hand).
"""
import os, sys

sys.path.insert(0, os.path.dirname(__file__))
from acceptance import API, ORG, call, error_message  # noqa: E402

DEV_ORG = next((l.split("=", 1)[1].strip().strip('"') for l in open(os.path.expanduser("~/.brokerforge/spike-ids.env")) if l.startswith("DV_ORG=")), None)


def gone(what, status, body, ok=(204,)):
    if status not in ok:
        sys.exit(f"{what}: HTTP {status}: {error_message(body)}")
    print(f"removed {what}", flush=True)


def main():
    # --any-org: the same clean-up in another test environment (DV_ORG set to it), named on purpose.
    if "--any-org" not in sys.argv and (not DEV_ORG or ORG.rstrip("/") != DEV_ORG.rstrip("/")):
        sys.exit("remove_old_inventory.py runs only against the dev org (DV_ORG in ~/.brokerforge/spike-ids.env); pass --any-org for another test environment")
    print(f"Dataverse: {API}", flush=True)
    _, body, _ = call("GET", "customapis?$select=customapiid&$filter=uniquename eq 'ext_AdjustStock'")
    for api in body.get("value", []):
        gone("Custom API ext_AdjustStock", *call("DELETE", f"customapis({api['customapiid']})")[:2])
    status, body, _ = call("GET", "EntityDefinitions(LogicalName='ext_item')?$select=MetadataId")
    if status == 200:
        gone("table ext_item", *call("DELETE", "EntityDefinitions(LogicalName='ext_item')")[:2])
    for row in call("GET", "bf_connections?$select=bf_connectionid&$filter=bf_name eq 'InventoryAPI'")[1].get("value", []):
        gone("connection row InventoryAPI", *call("DELETE", f"bf_connections({row['bf_connectionid']})")[:2])
    for role in call("GET", "roles?$select=roleid&$filter=name eq 'InventoryAPI user' and _parentroleid_value eq null")[1].get("value", []):
        gone("role InventoryAPI user", *call("DELETE", f"roles({role['roleid']})")[:2])
    for ds in call("GET", "bf_brokerdatasources?$select=bf_brokerdatasourceid&$filter=bf_serviceinstance eq 'InventoryAPI'")[1].get("value", []):
        gone("data source record of InventoryAPI", *call("DELETE", f"bf_brokerdatasources({ds['bf_brokerdatasourceid']})")[:2])
    for wr in call("GET", "webresourceset?$select=webresourceid&$filter=name eq 'ext_/brokerforge/InventoryAPI/design.xml'")[1].get("value", []):
        gone("design web resource of InventoryAPI", *call("DELETE", f"webresourceset({wr['webresourceid']})")[:2])
    for var in call("GET", "environmentvariabledefinitions?$select=environmentvariabledefinitionid,schemaname&$filter=startswith(schemaname,'bf_InventoryAPI_')")[1].get("value", []):
        gone(f"environment variable {var['schemaname']}", *call("DELETE", f"environmentvariabledefinitions({var['environmentvariabledefinitionid']})")[:2])
    for unique in ("BrokerForgeInventory", "BrokerForgeInventoryAPI"):
        _, body, _ = call("GET", f"solutions?$select=solutionid&$filter=uniquename eq '{unique}'")
        for s in body.get("value", []):
            gone(f"solution {unique}", *call("DELETE", f"solutions({s['solutionid']})")[:2])
    status, _, _ = call("GET", "ext_items?$top=1")
    print(f"GET ext_items: HTTP {status}", flush=True)


if __name__ == "__main__":
    main()
