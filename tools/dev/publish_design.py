"""Publish one service instance as a connection design (HLD D-34, docs/connection-design-format.md) in the dev environment.

Stopgap until plan 3's generator does this. It builds the design from `<root>/descriptors/<Instance>/*.yaml`,
`<root>/schemas/<Instance>.yaml`, `<root>/openapi/<Instance>.yaml` and the current host settings of an instance
(by default the same one; --settings-from takes another, for a copy such as WarehouseAPI from InventoryAPI), then:

- writes the web resource `ext_/brokerforge/<Instance>/design.xml` (type 4) into the solution and publishes it;
- creates `bf_<Instance>_BaseUrl` (String) and `bf_<Instance>_Auth` (JSON) in the solution with no default value;
- sets dev's base URL as the current value of `bf_<Instance>_BaseUrl` without the solution header, so dev's
  value does not travel with the solution (portal carrier findings, surprise 2).

Secrets are never read: the host setting `Auth:SecretRef` only tells it that the design needs the instance
secrets named by --secret-name, which an administrator sets with tools/dev/set-instance-secret.sh.

Usage: python3 publish_design.py <Instance> <Solution> [--root DIR] [--settings-from INSTANCE] [--secret-name NAME ...]
"""
import argparse, base64, json, os, subprocess, sys

sys.path.insert(0, os.path.dirname(__file__))
from setup_dataverse import DEV, call, first, step  # noqa: E402  (uses the az CLI user's Dataverse token)

PREFIX = "ext"
AUTH_FIELDS = ["mode", "staticKind", "headerName", "queryParam", "username", "scope", "ssoKind", "asyncContext"]


def host_settings():
    """The host container's plain settings (secret references are not values, but none is printed)."""
    env = json.loads(subprocess.check_output(["az", "containerapp", "show", "-g", DEV["RG"], "-n", "bf-dev-host",
                                              "--query", "properties.template.containers[0].env", "-o", "json"], text=True))
    return {e["name"]: e.get("value") for e in env}


def section(settings, prefix):
    return {k[len(prefix):]: v for k, v in settings.items() if k.startswith(prefix)}


def design_of(instance, root, source, secret_names):
    settings = host_settings()
    inst = section(settings, f"Instances__{source}__")
    if not inst:
        sys.exit(f"the host has no settings for instance {source}")
    auth = {f[0].lower() + f[1:]: v for f, v in section(inst, "Auth__").items()}
    if "oAuthResource" in auth:
        sys.exit(f"{source} uses an OAuth resource; this stopgap publishes only designs without one")
    if auth.pop("secretRef", None) and not secret_names:
        sys.exit(f"{source} reads a secret; name the instance secret(s) the design needs with --secret-name")
    unknown = sorted(set(auth) - set(AUTH_FIELDS))
    if unknown:
        sys.exit(f"{source} has auth settings this design format cannot carry: {', '.join(unknown)}")
    if any(k.startswith("Config__") or k.startswith("Secrets__") for k in inst):
        sys.exit(f"{source} has Config or Secrets settings, which the design format does not carry")

    maps = {}
    for k, v in settings.items():
        if k.startswith("ObjectMaps__") and k.endswith("__ServiceInstance") and v == source:
            maps[k[len("ObjectMaps__"):-len("__ServiceInstance")]] = section(settings, k[:-len("ServiceInstance")])
    if len(maps) > 1:
        sys.exit(f"{source} maps more than one table")

    schema_text = open(os.path.join(root, "schemas", f"{instance}.yaml"), encoding="utf-8").read()
    table = next(line.split(":", 1)[1].strip() for line in schema_text.splitlines() if line.startswith("table:"))
    descriptors_dir = os.path.join(root, "descriptors", instance)
    descriptors = {f[:-5]: open(os.path.join(descriptors_dir, f), encoding="utf-8").read()
                   for f in sorted(os.listdir(descriptors_dir)) if f.endswith(".yaml")}
    design = {
        "formatVersion": 1,
        "instance": instance,
        "version": 1,
        "brokerType": inst["BrokerType"],
        "openApi": open(os.path.join(root, "openapi", f"{instance}.yaml"), encoding="utf-8").read(),
        "answers": {"prefix": PREFIX, "table": table},
        "auth": dict({f: auth[f] for f in AUTH_FIELDS if auth.get(f)}, secretNames=secret_names),
    }
    if maps:
        (_, m), = maps.items()
        design["objectMap"] = {"table": table, "keyStrategy": m["KeyStrategy"], "keyKind": m["KeyKind"],
                               "operations": dict(sorted(section(m, "Operations__").items()))}
    design["schema"] = schema_text
    design["descriptors"] = dict(sorted(descriptors.items()))
    return design, inst["BaseUrl"]


def ensure_variable(schema, display, kind, solution):
    d = first(f"environmentvariabledefinitions?$select=environmentvariabledefinitionid&$filter=schemaname eq '{schema}'")
    if d:
        return d["environmentvariabledefinitionid"]
    _, body, _ = call("POST", "environmentvariabledefinitions", {"schemaname": schema, "displayname": display, "type": kind}, solution)
    return body["environmentvariabledefinitionid"]


def set_current_value(def_id, schema, value):
    """Dev's own value, created without the solution header so that it stays in this environment."""
    v = first(f"environmentvariablevalues?$select=environmentvariablevalueid,value&$filter=_environmentvariabledefinitionid_value eq {def_id}")
    if not v:
        call("POST", "environmentvariablevalues", {"schemaname": schema, "value": value,
                                                   "EnvironmentVariableDefinitionId@odata.bind": f"/environmentvariabledefinitions({def_id})"})
    elif v.get("value") != value:
        call("PATCH", f"environmentvariablevalues({v['environmentvariablevalueid']})", {"value": value})


def main():
    p = argparse.ArgumentParser()
    p.add_argument("instance")
    p.add_argument("solution")
    p.add_argument("--root", default=os.path.join(os.path.dirname(__file__), "..", ".."))
    p.add_argument("--settings-from")
    p.add_argument("--secret-name", action="append", default=[])
    a = p.parse_args()

    design, base_url = design_of(a.instance, a.root, a.settings_from or a.instance, a.secret_name)
    name = f"{PREFIX}_/brokerforge/{a.instance}/design.xml"
    existing = first(f"webresourceset?$select=webresourceid,content&$filter=name eq '{name}'")
    if existing and existing.get("content"):
        old = base64.b64decode(existing["content"]).decode("utf-8")
        old_json = old[old.index("<![CDATA[") + 9:old.rindex("]]>")]
        old_design = json.loads(old_json)
        design["version"] = old_design["version"]
        if {k: v for k, v in old_design.items()} != design:
            design["version"] += 1
    text = json.dumps(design, indent=2, ensure_ascii=False).replace("]]>", "]]\\u003E")
    wrapped = f'<brokerforgeDesign formatVersion="1"><![CDATA[{text}]]></brokerforgeDesign>'
    content = base64.b64encode(wrapped.encode("utf-8")).decode()

    step(f"design {name} v{design['version']} ({len(wrapped.encode('utf-8'))} bytes)")
    if existing:
        wr_id = existing["webresourceid"]
        call("PATCH", f"webresourceset({wr_id})", {"content": content})
    else:
        _, body, _ = call("POST", "webresourceset", {"name": name, "displayname": f"BrokerForge design {a.instance}",
                                                     "webresourcetype": 4, "content": content}, a.solution)
        wr_id = body["webresourceid"]
    # An update stays invisible to reads until it is published (portal carrier findings, surprise 1).
    call("POST", "PublishXml", {"ParameterXml": f"<importexportxml><webresources><webresource>{{{wr_id}}}</webresource></webresources></importexportxml>"})

    step(f"environment variables bf_{a.instance}_BaseUrl and bf_{a.instance}_Auth")
    url_id = ensure_variable(f"bf_{a.instance}_BaseUrl", f"BrokerForge {a.instance} base URL", 100000000, a.solution)
    ensure_variable(f"bf_{a.instance}_Auth", f"BrokerForge {a.instance} auth overrides", 100000003, a.solution)
    set_current_value(url_id, f"bf_{a.instance}_BaseUrl", base_url)
    print(f"done: {a.instance} design v{design['version']} in {a.solution}; set its secrets with tools/dev/set-instance-secret.sh")


if __name__ == "__main__":
    main()
