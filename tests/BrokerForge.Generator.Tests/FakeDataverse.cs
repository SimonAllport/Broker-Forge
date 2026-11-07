using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BrokerForge.Generator.Tests;

/// <summary>
/// An in-memory stand-in for the Dataverse Web API endpoints the generator calls. It keeps just enough
/// metadata state for re-runs to see what an earlier run created, and records every request.
/// </summary>
internal sealed partial class FakeDataverse : HttpMessageHandler
{
    public const string Org = "https://org.example.crm.dynamics.com";
    public const string ProviderId = "4c75d224-0000-0000-0000-000000000001";
    public const string CustomApiPluginTypeId = "7d000000-0000-0000-0000-0000000000a1";

    public sealed record Recorded(string Method, string Path, string? Body, string? Solution, string? Authorization);

    public List<Recorded> Requests { get; } = new();

    /// <summary>How many of the next PublishXml calls fail with 500.</summary>
    public int FailPublishes { get; set; }
    public IEnumerable<Recorded> Writes => Requests.Where(r => r.Method != "GET");
    public bool DropProviderOnCreate { get; set; }
    public const string BusinessUnitId = "00000000-0000-0000-0000-0000000000b0";

    /// <summary>The write (counting from 1) that fails once with HTTP 500; 0 for none.</summary>
    public int FailWrite { get; set; }

    /// <summary>The write (counting from 1) that fails with a network error, as an HttpClient does; 0 for none.</summary>
    public int ThrowWrite { get; set; }
    private int _writeCount;

    private readonly Dictionary<string, JsonObject> _forms = new();
    private readonly Dictionary<string, JsonObject> _views = new();
    private readonly List<(string Table, string Column, string ViewId, string Name)> _handViews = new();
    private readonly List<JsonObject> _roles = new();
    private readonly Dictionary<string, string> _privileges = new();
    private readonly Dictionary<string, Dictionary<string, int>> _rolePrivileges = new();
    private readonly List<(string Solution, int Type, string Id)> _components = new();

    /// <summary>The components of a solution, by type: what an export of it would carry.</summary>
    public IEnumerable<string> Components(string solution, int type) => _components.Where(c => c.Solution == solution && c.Type == type).Select(c => c.Id);

    public IReadOnlyList<JsonObject> DataSources => _dataSources;
    private readonly List<JsonObject> _webResources = new();
    private readonly List<JsonObject> _variables = new();
    private readonly List<JsonObject> _values = new();
    private readonly List<(string Table, string Column, int Type, string Id, int DependencyType)> _dependents = new();
    private readonly Dictionary<int, string> _definitions = new();

    /// <summary>
    /// Routes a test adds for tables the generator never touches (connection rows, users, teams); null falls through.
    /// Asked before the built-in routes, so it may also answer a query they would (the core solution, for example).
    /// </summary>
    public Func<string, string, JsonObject?, HttpResponseMessage?>? ExtraRoute { get; set; }

    /// <summary>Each request waits this long first (cancellable), to hold a job open.</summary>
    public TimeSpan Delay { get; set; }

    /// <summary>An environment variable with a current value, as the core setup leaves bf_HostUrl and bf_HostScope.</summary>
    public void SetVariable(string schema, string value)
    {
        var id = NewId();
        _variables.Add(new JsonObject { ["environmentvariabledefinitionid"] = id, ["schemaname"] = schema, ["defaultvalue"] = null });
        _values.Add(new JsonObject { ["environmentvariablevalueid"] = NewId(), ["_environmentvariabledefinitionid_value"] = id, ["value"] = value });
    }

    public JsonObject WebResource(string name) => _webResources.Single(w => (string)w["name"]! == name);

    public string? VariableValue(string schema)
    {
        var id = (string)_variables.Single(v => (string)v["schemaname"]! == schema)["environmentvariabledefinitionid"]!;
        return (string?)_values.SingleOrDefault(v => (string)v["_environmentvariabledefinitionid_value"]! == id)?["value"];
    }

    /// <summary>A hand-made public view on <paramref name="table"/> that uses <paramref name="column"/>.</summary>
    public string AddHandView(string table, string column, string name)
    {
        var id = NewId();
        _handViews.Add((table, column, id, name));
        return id;
    }

    public void RemoveHandViews() => _handViews.Clear();

    /// <summary>Another component that uses <paramref name="column"/>, of any type and dependency type.</summary>
    public void AddDependent(string table, string column, int type, string id, int dependencyType = 2) =>
        _dependents.Add((table, column, type, id, dependencyType));

    /// <summary>A solution component type the platform defines in <c>solutioncomponentdefinitions</c> (types from 10000).</summary>
    public void AddComponentDefinition(int type, string name) => _definitions[type] = name;

    /// <summary>A role made by hand, outside any solution.</summary>
    public string AddRole(string name)
    {
        var id = NewId();
        _roles.Add(new JsonObject { ["roleid"] = id, ["name"] = name });
        _rolePrivileges[id] = new();
        return id;
    }

    public JsonObject MainForm(string table) => _forms[table];
    public JsonObject DefaultView(string table) => _views[table];
    public IReadOnlyDictionary<string, int> RolePrivileges(string roleName) =>
        _rolePrivileges[(string)_roles.Single(r => (string)r["name"]! == roleName)["roleid"]!]
            .ToDictionary(p => _privileges.Single(x => x.Value == p.Key).Key, p => p.Value);
    public void GrantRolePrivilege(string roleName, string privilege, int depth) =>
        _rolePrivileges[(string)_roles.Single(r => (string)r["name"]! == roleName)["roleid"]!][_privileges[privilege]] = depth;

    private readonly Dictionary<string, string> _publishers = new();
    private readonly Dictionary<string, string> _solutions = new();
    private readonly List<JsonObject> _dataSources = new();
    private readonly Dictionary<string, JsonObject> _tables = new();
    private readonly Dictionary<string, List<JsonObject>> _attributes = new();
    private readonly List<JsonObject> _customApis = new();
    private int _ids;

    private string NewId() => $"00000000-0000-0000-0000-{++_ids:D12}";

    public void RenameDataSources(string instance)
    {
        foreach (var d in _dataSources)
            d["bf_serviceinstance"] = instance;
    }

    public void SetCustomApi(string uniqueName, string key, JsonNode value) =>
        _customApis.Single(a => (string)a["uniquename"]! == uniqueName)[key] = value;

    public void RemoveAttribute(string table, string logical) => _attributes[table].RemoveAll(a => (string)a["LogicalName"]! == logical);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Delay > TimeSpan.Zero)
            await Task.Delay(Delay, ct);
        var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
        var path = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery)["/api/data/v9.2/".Length..];
        var solution = request.Headers.TryGetValues("MSCRM.SolutionUniqueName", out var s) ? s.Single() : null;
        lock (Requests)
            Requests.Add(new Recorded(request.Method.Method, path, body, solution, request.Headers.Authorization?.ToString()));
        if (request.Method != HttpMethod.Get && ++_writeCount == ThrowWrite)
            throw new HttpRequestException("injected network failure");
        if (request.Method != HttpMethod.Get && _writeCount == FailWrite)
            return Json(new JsonObject { ["error"] = new JsonObject { ["message"] = "injected failure" } }, HttpStatusCode.InternalServerError);
        var json = body == null ? null : JsonNode.Parse(body)!.AsObject();
        return Route(request.Method.Method, path, json, solution);
    }

    private static HttpResponseMessage Json(JsonNode node, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Value(IEnumerable<JsonNode> rows) => Json(new JsonObject { ["value"] = new JsonArray(rows.Select(r => r.DeepClone()).ToArray()) });

    private static HttpResponseMessage Status(HttpStatusCode status) => new(status);

    [GeneratedRegex(@"(?:eq |contains\(name,)'((?:[^']|'')+)'")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"^EntityDefinitions\(LogicalName='([^']+)'\)(.*)$")]
    private static partial Regex Entity();

    [GeneratedRegex(@"^/Attributes\(LogicalName='([^']+)'\)(\?.*)?$")]
    private static partial Regex Single();

    [GeneratedRegex(@"RetrieveDependenciesForDelete\(ObjectId=([^,]+),ComponentType=(\d+)\)")]
    private static partial Regex DependenciesCall();

    private HttpResponseMessage Route(string method, string path, JsonObject? body, string? inSolution)
    {
        if (ExtraRoute?.Invoke(method, path, body) is { } early)
            return early;
        var name = Quoted().Match(path) is { Success: true } q ? q.Groups[1].Value.Replace("''", "'") : null;
        if (DependenciesCall().Match(path) is { Success: true } dep)
            return Value(Dependents(dep.Groups[1].Value, int.Parse(dep.Groups[2].Value)).Select(d => (JsonNode)new JsonObject
            {
                ["dependentcomponenttype"] = d.Type, ["dependentcomponentobjectid"] = d.Id, ["dependencytype"] = d.DependencyType,
            }));
        switch (method, path)
        {
            case ("GET", _) when path.StartsWith("solutioncomponentdefinitions?"):
                return Value(_definitions.Where(d => path.Contains($"solutioncomponenttype eq {d.Key}"))
                    .Select(d => (JsonNode)new JsonObject { ["solutioncomponenttype"] = d.Key, ["name"] = d.Value }));
            case ("GET", _) when path.StartsWith("systemforms?"):
                return Value(_forms.TryGetValue(name!, out var form) ? new[] { form } : Array.Empty<JsonNode>());
            case ("GET", _) when path.StartsWith("systemforms("):
                return Json(_forms.Values.Single(f => path.StartsWith($"systemforms({f["formid"]})")).DeepClone());
            case ("PATCH", _) when path.StartsWith("systemforms("):
                _forms.Values.Single(f => path == $"systemforms({f["formid"]})")["formxml"] = body!["formxml"]!.DeepClone();
                return Status(HttpStatusCode.NoContent);
            case ("GET", _) when path.StartsWith("savedqueries?"):
                return Value(_views.TryGetValue(name!, out var view) ? new[] { view } : Array.Empty<JsonNode>());
            case ("GET", _) when path.StartsWith("savedqueries("):
                var hand = _handViews.FirstOrDefault(h => path.StartsWith($"savedqueries({h.ViewId})"));
                return hand.ViewId != null ? Json(new JsonObject { ["name"] = hand.Name })
                    : Json(_views.Values.Single(v => path.StartsWith($"savedqueries({v["savedqueryid"]})")).DeepClone());
            case ("PATCH", _) when path.StartsWith("savedqueries("):
                var patched = _views.Values.Single(v => path == $"savedqueries({v["savedqueryid"]})");
                patched["fetchxml"] = body!["fetchxml"]!.DeepClone();
                patched["layoutxml"] = body["layoutxml"]!.DeepClone();
                return Status(HttpStatusCode.NoContent);
            case ("GET", _) when path.StartsWith("webresourceset?"):
                return Value(_webResources.Where(w => path.Contains("contains(name,") ? ((string)w["name"]!).Contains(name!) : (string)w["name"]! == name));
            case ("POST", "webresourceset"):
                body!["webresourceid"] = NewId();
                _webResources.Add(body);
                return Json(body.DeepClone(), HttpStatusCode.Created);
            case ("PATCH", _) when path.StartsWith("webresourceset("):
                var resource = _webResources.Single(w => path == $"webresourceset({w["webresourceid"]})");
                foreach (var (k, v) in body!)
                    resource[k] = v?.DeepClone();
                return Status(HttpStatusCode.NoContent);
            case ("GET", _) when path.StartsWith("environmentvariabledefinitions?"):
                return Value(_variables.Where(v => (string)v["schemaname"]! == name));
            case ("POST", "environmentvariabledefinitions"):
                body!["environmentvariabledefinitionid"] = NewId();
                _variables.Add(body);
                return Json(body.DeepClone(), HttpStatusCode.Created);
            case ("GET", _) when path.StartsWith("environmentvariablevalues?"):
                var definition = path[(path.IndexOf("_environmentvariabledefinitionid_value eq ", StringComparison.Ordinal) + "_environmentvariabledefinitionid_value eq ".Length)..];
                return Value(_values.Where(v => (string)v["_environmentvariabledefinitionid_value"]! == definition));
            case ("POST", "environmentvariablevalues"):
                body!["environmentvariablevalueid"] = NewId();
                body["_environmentvariabledefinitionid_value"] = ((string)body["EnvironmentVariableDefinitionId@odata.bind"]!)["/environmentvariabledefinitions(".Length..^1];
                _values.Add(body);
                return Json(body.DeepClone(), HttpStatusCode.Created);
            case ("PATCH", _) when path.StartsWith("environmentvariablevalues("):
                _values.Single(v => path == $"environmentvariablevalues({v["environmentvariablevalueid"]})")["value"] = body!["value"]!.DeepClone();
                return Status(HttpStatusCode.NoContent);
            case ("GET", _) when path.StartsWith("businessunits?"):
                return Value(new[] { new JsonObject { ["businessunitid"] = BusinessUnitId } });
            case ("GET", _) when path.StartsWith("roles?"):
                return Value(_roles.Where(r => (string)r["name"]! == name));
            case ("POST", "roles"):
                var role = new JsonObject { ["roleid"] = NewId(), ["name"] = body!["name"]!.DeepClone() };
                _roles.Add(role);
                _rolePrivileges[(string)role["roleid"]!] = new() { [Privilege("prvReadPluginType")] = 8 };
                if (inSolution != null)
                    _components.Add((inSolution, 20, (string)role["roleid"]!));
                return Json(role.DeepClone(), HttpStatusCode.Created);
            case ("GET", _) when path.StartsWith("privileges?"):
                return Value(Quoted().Matches(path).Select(m => m.Groups[1].Value).Where(_privileges.ContainsKey)
                    .Select(n => (JsonNode)new JsonObject { ["name"] = n, ["privilegeid"] = _privileges[n] }));
            case ("GET", _) when path.StartsWith("roleprivilegescollection?"):
                var heldBy = path[(path.IndexOf("roleid eq ", StringComparison.Ordinal) + "roleid eq ".Length)..];
                return Value(_rolePrivileges[heldBy].Select(p => (JsonNode)new JsonObject { ["privilegeid"] = p.Key, ["privilegedepthmask"] = p.Value }));
            case ("POST", _) when path.EndsWith("/Microsoft.Dynamics.CRM.AddPrivilegesRole"):
                foreach (var p in body!["Privileges"]!.AsArray())
                    _rolePrivileges[path["roles(".Length..path.IndexOf(')')]][(string)p!["PrivilegeId"]!] = (string)p["Depth"]! == "Global" ? 8 : 1;
                return Status(HttpStatusCode.NoContent);
            case ("POST", _) when path.EndsWith("/Microsoft.Dynamics.CRM.RemovePrivilegeRole"):
                _rolePrivileges[path["roles(".Length..path.IndexOf(')')]].Remove((string)body!["Privilege"]!["privilegeid"]!);
                return Status(HttpStatusCode.NoContent);
            case ("GET", _) when path.StartsWith("solutioncomponents?"):
                var inId = path[(path.IndexOf("_solutionid_value eq ", StringComparison.Ordinal) + "_solutionid_value eq ".Length)..].Split(' ')[0];
                var unique = _solutions.Single(x => x.Value == inId).Key;
                return Value(_components.Where(c => c.Solution == unique && path.Contains($"componenttype eq {c.Type}")).Select(c => (JsonNode)new JsonObject { ["objectid"] = c.Id }));
            case ("GET", _) when path.StartsWith("customapis("):
                var byId = _customApis.SingleOrDefault(a => path.StartsWith($"customapis({a["customapiid"]})"));
                return byId == null ? Status(HttpStatusCode.NotFound) : Json(byId.DeepClone());
            case ("DELETE", _) when path.StartsWith("customapis("):
                var removedApi = _customApis.SingleOrDefault(a => path == $"customapis({a["customapiid"]})");
                if (removedApi == null)
                    return Status(HttpStatusCode.NotFound);
                _customApis.Remove(removedApi);
                _components.RemoveAll(c => c.Id == (string)removedApi["customapiid"]!);
                return Status(HttpStatusCode.NoContent);
            case ("PATCH", _) when path.StartsWith("customapis("):
                var api = _customApis.Single(a => path == $"customapis({a["customapiid"]})");
                foreach (var (k, v) in body!)
                    api[k] = v?.DeepClone();
                return Status(HttpStatusCode.NoContent);
            case ("GET", _) when path.StartsWith("entitydataproviders?"):
                return Value(name == "BrokerForge" ? new[] { new JsonObject { ["entitydataproviderid"] = ProviderId } } : Array.Empty<JsonNode>());
            case ("GET", _) when path.StartsWith("plugintypes?"):
                return Value(name == "BrokerForge.Plugin.CustomApiPlugin" ? new[] { new JsonObject { ["plugintypeid"] = CustomApiPluginTypeId } } : Array.Empty<JsonNode>());
            case ("GET", _) when path.StartsWith("publishers?"):
                return Value(_publishers.TryGetValue(name!, out var pid) ? new[] { new JsonObject { ["publisherid"] = pid } } : Array.Empty<JsonNode>());
            case ("POST", "publishers"):
                var publisher = _publishers[(string)body!["uniquename"]!] = NewId();
                return Json(new JsonObject { ["publisherid"] = publisher }, HttpStatusCode.Created);
            case ("GET", _) when path.StartsWith("solutions?"):
                return Value(_solutions.TryGetValue(name!, out var sid) ? new[] { new JsonObject { ["solutionid"] = sid } } : Array.Empty<JsonNode>());
            case ("POST", "solutions"):
                var solution = _solutions[(string)body!["uniquename"]!] = NewId();
                return Json(new JsonObject { ["solutionid"] = solution }, HttpStatusCode.Created);
            case ("POST", "AddSolutionComponent"):
                _components.Add(((string)body!["SolutionUniqueName"]!, (int)body["ComponentType"]!, (string)body["ComponentId"]!));
                return Json(new JsonObject { ["id"] = NewId() });
            case ("GET", _) when path.StartsWith("bf_brokerdatasources?"):
                return Value(_dataSources.Where(d => (string)d["bf_serviceinstance"]! == name));
            case ("POST", "bf_brokerdatasources"):
                body!["bf_brokerdatasourceid"] = NewId();
                _dataSources.Add(body);
                return Json(body.DeepClone(), HttpStatusCode.Created);
            case ("PATCH", _) when path.StartsWith("bf_brokerdatasources("):
                var id = path["bf_brokerdatasources(".Length..^1];
                var row = _dataSources.Single(d => (string)d["bf_brokerdatasourceid"]! == id);
                foreach (var (k, v) in body!)
                    row[k] = v?.DeepClone();
                return Status(HttpStatusCode.NoContent);
            case ("POST", "EntityDefinitions"):
                return CreateTable(body!);
            case ("POST", "PublishXml") when FailPublishes > 0:
                FailPublishes--;
                return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("""{"error":{"message":"publish failed"}}""") };
            case ("POST", "PublishXml"):
                return Status(HttpStatusCode.NoContent);
            case ("GET", _) when path.StartsWith("customapis?"):
                return Value(_customApis.Where(a => (string)a["uniquename"]! == name));
            case ("POST", "customapis"):
                body!["customapiid"] = NewId();
                body["_plugintypeid_value"] = ((string)body["PluginTypeId@odata.bind"]!)["/plugintypes(".Length..^1];
                _customApis.Add(body);
                if (inSolution != null)
                    _components.Add((inSolution, 10038, (string)body["customapiid"]!));
                return Status(HttpStatusCode.NoContent);
        }
        if (Entity().Match(path) is { Success: true } e)
            return EntityRoute(method, e.Groups[1].Value, e.Groups[2].Value, body);
        throw new InvalidOperationException($"FakeDataverse has no route for {method} {path}");
    }

    private HttpResponseMessage CreateTable(JsonObject body)
    {
        var logical = ((string)body["SchemaName"]!).ToLowerInvariant();
        _tables[logical] = new JsonObject
        {
            ["TableType"] = "Virtual",
            ["DataProviderId"] = DropProviderOnCreate ? null : body["DataProviderId"]?.DeepClone(),
            ["DataSourceId"] = body["DataSourceId"]?.DeepClone(),
            ["ExternalName"] = body["ExternalName"]?.DeepClone(),
            ["PrimaryNameAttribute"] = ((string?)body["Attributes"]!.AsArray().FirstOrDefault(a => (bool?)a!["IsPrimaryName"] == true)?["SchemaName"])?.ToLowerInvariant(),
            ["DisplayName"] = body["DisplayName"]?.DeepClone(),
            ["DisplayCollectionName"] = body["DisplayCollectionName"]?.DeepClone(),
        };
        _attributes[logical] = new List<JsonObject>
        {
            new() { ["LogicalName"] = logical + "id", ["AttributeType"] = "Uniqueidentifier", ["IsCustomAttribute"] = true, ["ExternalName"] = null },
        };
        foreach (var attribute in body["Attributes"]!.AsArray())
            AddAttribute(logical, attribute!.AsObject());
        var primary = (string)_tables[logical]["PrimaryNameAttribute"]!;
        _forms[logical] = new JsonObject
        {
            ["formid"] = NewId(),
            ["formxml"] = $"<form><tabs><tab><columns><column><sections><section><rows><row><cell><control id=\"{primary}\" datafieldname=\"{primary}\" /></cell></row></rows></section></sections></column></columns></tab></tabs></form>",
        };
        var viewId = NewId();
        _views[logical] = new JsonObject
        {
            ["savedqueryid"] = viewId,
            ["fetchxml"] = $"<fetch version=\"1.0\" mapping=\"logical\"><entity name=\"{logical}\"><attribute name=\"{logical}id\" /><attribute name=\"{primary}\" /></entity></fetch>",
            ["layoutxml"] = $"<grid name=\"resultset\" object=\"10742\" jump=\"{primary}\" select=\"1\" icon=\"1\" preview=\"1\"><row name=\"result\" id=\"{logical}id\"><cell name=\"{primary}\" width=\"300\" /></row></grid>",
        };
        foreach (var verb in new[] { "Read", "Create", "Write", "Delete", "Append", "AppendTo" })
            Privilege($"prv{verb}{logical}");
        return Status(HttpStatusCode.NoContent);
    }

    private string Privilege(string name) => _privileges.TryGetValue(name, out var id) ? id : _privileges[name] = NewId();

    /// <summary>What uses an attribute (by MetadataId) or a Custom API: the generated form and view by their XML, and hand-made views.</summary>
    private IEnumerable<(int Type, string Id, int DependencyType)> Dependents(string objectId, int componentType)
    {
        if (componentType != 2)
            yield break;
        foreach (var (table, attributes) in _attributes)
        {
            var column = (string?)attributes.FirstOrDefault(a => (string?)a["MetadataId"] == objectId)?["LogicalName"];
            if (column == null)
                continue;
            if (_forms.TryGetValue(table, out var form) && ((string)form["formxml"]!).Contains($"datafieldname=\"{column}\""))
                yield return (60, (string)form["formid"]!, 2);
            if (_views.TryGetValue(table, out var view) && ((string)view["layoutxml"]!).Contains($"<cell name=\"{column}\""))
                yield return (26, (string)view["savedqueryid"]!, 2);
            foreach (var hand in _handViews.Where(h => h.Table == table && h.Column == column))
                yield return (26, hand.ViewId, 2);
            foreach (var other in _dependents.Where(d => d.Table == table && d.Column == column))
                yield return (other.Type, other.Id, other.DependencyType);
        }
    }

    private void AddAttribute(string table, JsonObject payload)
    {
        var type = ((string)payload["@odata.type"]!).Replace("Microsoft.Dynamics.CRM.", "").Replace("AttributeMetadata", "");
        _attributes[table].Add(new JsonObject
        {
            ["LogicalName"] = ((string)payload["SchemaName"]!).ToLowerInvariant(),
            ["AttributeType"] = type,
            ["IsCustomAttribute"] = true,
            ["ExternalName"] = payload["ExternalName"]?.DeepClone(),
            ["MaxLength"] = payload["MaxLength"]?.DeepClone(),
            ["Precision"] = payload["Precision"]?.DeepClone(),
            ["Format"] = payload["Format"]?.DeepClone(),
            ["OptionSet"] = payload["OptionSet"]?.DeepClone(),
            ["DisplayName"] = payload["DisplayName"]?.DeepClone(),
            ["MetadataId"] = NewId(),
            ["@odata.type"] = payload["@odata.type"]?.DeepClone(),
        });
        // Live: the platform adds a derived <column>name attribute for every picklist (phase 2 findings).
        if (type == "Picklist")
            _attributes[table].Add(new JsonObject
            {
                ["LogicalName"] = ((string)payload["SchemaName"]!).ToLowerInvariant() + "name",
                ["AttributeType"] = "Virtual",
                ["IsCustomAttribute"] = true,
                ["AttributeOf"] = ((string)payload["SchemaName"]!).ToLowerInvariant(),
            });
    }

    /// <summary>Replaces one stored attribute's AttributeType, as if the column had been created differently.</summary>
    public void SetAttributeType(string table, string logical, string type) =>
        _attributes[table].Single(a => (string)a["LogicalName"]! == logical)["AttributeType"] = type;

    private HttpResponseMessage EntityRoute(string method, string table, string rest, JsonObject? body)
    {
        if (!_tables.TryGetValue(table, out var t))
            return Status(HttpStatusCode.NotFound);
        if (method == "GET" && (rest.StartsWith("?") || rest == ""))
            return Json(t.DeepClone());
        if (method == "PUT" && rest == "")
        {
            t["DisplayName"] = body!["DisplayName"]!.DeepClone();
            t["DisplayCollectionName"] = body["DisplayCollectionName"]!.DeepClone();
            return Status(HttpStatusCode.NoContent);
        }
        if (Single().Match(rest) is { Success: true } one)
        {
            var attribute = _attributes[table].SingleOrDefault(a => (string)a["LogicalName"]! == one.Groups[1].Value);
            switch (method)
            {
                case "GET":
                    return attribute == null ? Status(HttpStatusCode.NotFound) : Json(attribute.DeepClone());
                case "PUT":
                    attribute!["DisplayName"] = body!["DisplayName"]!.DeepClone();
                    return Status(HttpStatusCode.NoContent);
                case "DELETE":
                    if (attribute == null)
                        return Status(HttpStatusCode.NotFound);
                    var users = Dependents((string)attribute["MetadataId"]!, 2).Where(d => d.DependencyType != 1).ToList();
                    if (users.Count > 0)
                        return Json(new JsonObject { ["error"] = new JsonObject { ["message"] = $"referenced by {users.Count} other components" } }, HttpStatusCode.BadRequest);
                    _attributes[table].Remove(attribute);
                    return Status(HttpStatusCode.NoContent);
            }
        }
        if (method == "POST" && rest == "/Attributes")
        {
            AddAttribute(table, body!);
            return Status(HttpStatusCode.NoContent);
        }
        if (method == "GET" && rest.StartsWith("/Attributes?"))
            return Value(_attributes[table].Select(a => new JsonObject
            {
                ["LogicalName"] = a["LogicalName"]?.DeepClone(), ["AttributeType"] = a["AttributeType"]?.DeepClone(),
                ["IsCustomAttribute"] = a["IsCustomAttribute"]?.DeepClone(), ["ExternalName"] = a["ExternalName"]?.DeepClone(),
                ["AttributeOf"] = a["AttributeOf"]?.DeepClone(), ["DisplayName"] = a["DisplayName"]?.DeepClone(), ["MetadataId"] = a["MetadataId"]?.DeepClone(),
            }));
        if (method == "GET" && rest.StartsWith("/Attributes/Microsoft.Dynamics.CRM."))
        {
            var cast = rest["/Attributes/Microsoft.Dynamics.CRM.".Length..rest.IndexOf('?')].Replace("AttributeMetadata", "");
            return Value(_attributes[table].Where(a => (string)a["AttributeType"]! == cast).Select(a => a.DeepClone()));
        }
        throw new InvalidOperationException($"FakeDataverse has no route for {method} EntityDefinitions({table}){rest}");
    }
}
