using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BrokerForge.Host.Admin;
using Fake = BrokerForge.Generator.Tests.FakeDataverse;

namespace BrokerForge.Contract.Tests.Admin;

/// <summary>
/// The admin side of one environment on top of <see cref="Fake"/>: the core solution with its administrator role, users
/// and teams with roles, and <c>bf_connection</c> rows. All IDs are made up.
/// </summary>
internal sealed partial class AdminDataverse
{
    public const string CoreSolutionId = "00000000-0000-0000-0000-0000000c0de0";
    public const string AdminRoleId = "00000000-0000-0000-0000-0000000ad010";
    public const string AdminOid = "00000000-0000-0000-0000-00000000a001";

    public Fake Fake { get; } = new();
    public Dictionary<Guid, JsonObject> Rows { get; } = new();
    public List<(string Method, string Path, JsonObject? Body)> RowWrites { get; } = new();
    /// <summary>Set to make a row write fail with HTTP 500.</summary>
    public bool FailRowWrites { get; set; }

    private readonly List<(string Id, string Name)> _namedRoles = new() { (AdminRoleId, AdminCheck.RoleName) };
    private readonly Dictionary<string, (string UserId, List<(string RoleId, string Root)> Roles, List<string> Teams)> _users = new();
    private readonly HashSet<string> _disabled = new();

    /// <summary>The user is disabled in Dataverse, as one removed from the environment is, but keeps their roles.</summary>
    public void Disable(string oid) => _disabled.Add(oid);
    private readonly Dictionary<string, List<(string RoleId, string Root)>> _teams = new();
    private int _ids;

    public AdminDataverse()
    {
        Fake.SetVariable("bf_HostUrl", "https://bf-host.contoso.example");
        Fake.SetVariable("bf_HostScope", "api://00000000-0000-0000-0000-0000000000c1/.default");
        Fake.ExtraRoute = Route;
        AddUser(AdminOid, AdminRoleId);
    }

    private string NewId() => $"00000000-0000-0000-0000-0000000e{++_ids:D4}";

    /// <summary>A user with the given role copies, each named by its root role.</summary>
    public string AddUser(string oid, params string[] roots)
    {
        var id = NewId();
        _users[oid] = (id, roots.Select(r => (NewId(), r)).ToList(), new());
        return id;
    }

    public void AddTeamRole(string oid, string root)
    {
        var team = NewId();
        _teams[team] = new() { (NewId(), root) };
        _users[oid].Teams.Add(team);
    }

    /// <summary>A role named BrokerForge administrator outside the core solution; returns its root ID.</summary>
    public string AddHandMadeAdminRole()
    {
        var id = NewId();
        _namedRoles.Insert(0, (id, AdminCheck.RoleName));
        return id;
    }

    public Guid AddRow(ConnectionRow row)
    {
        Rows[row.Id] = new JsonObject
        {
            ["bf_connectionid"] = row.Id.ToString("D"), ["bf_name"] = row.Name, ["bf_baseurl"] = row.BaseUrl,
            ["bf_authmode"] = ConnectionRow.Modes.Single(m => m.Value == row.AuthMode).Key, ["bf_statickind"] = row.StaticKind, ["bf_headername"] = row.HeaderName,
            ["bf_queryparam"] = row.QueryParam, ["bf_username"] = row.Username, ["bf_scope"] = row.Scope, ["bf_ssokind"] = row.SsoKind,
            ["bf_authorizeurl"] = row.AuthorizeUrl, ["bf_tokenurl"] = row.TokenUrl, ["bf_oauthscopes"] = row.OAuthScopes, ["bf_clientid"] = row.ClientId,
            ["bf_usesconnectapp"] = row.UsesConnectApp, ["bf_prefix"] = row.Prefix, ["bf_solution"] = row.Solution, ["bf_openapi"] = row.OpenApi,
            ["bf_answers"] = row.AnswersJson,
        };
        return row.Id;
    }

    [GeneratedRegex(@"^bf_connections\(([0-9a-f-]{36})\)")]
    private static partial Regex RowPath();

    // Dataverse compares strings without regard to case.
    [GeneratedRegex(@"bf_name eq '((?:[^']|'')+)'")]
    private static partial Regex BfName();

    [GeneratedRegex(@"azureactivedirectoryobjectid eq ([0-9a-f-]{36})")]
    private static partial Regex Oid();

    [GeneratedRegex(@"^(systemusers|teams)\(([0-9a-f-]{36})\)/(systemuserroles_association|teammembership_association|teamroles_association)")]
    private static partial Regex Related();

    /// <summary>Requests whose path starts with this answer 403, as Dataverse does when the host role lacks a privilege.</summary>
    public string? Forbid { get; set; }

    private HttpResponseMessage? Route(string method, string path, JsonObject? body)
    {
        if (Forbid != null && path.StartsWith(Forbid, StringComparison.Ordinal))
            return Json(new JsonObject { ["error"] = new JsonObject { ["code"] = "0x80040220", ["message"] = "SecLib::CheckPrivilege failed (test)" } }, HttpStatusCode.Forbidden);
        if (RowPath().Match(path) is { Success: true } r)
        {
            var id = Guid.Parse(r.Groups[1].Value);
            if (method == "GET")
                return Rows.TryGetValue(id, out var row) ? Json(row) : new HttpResponseMessage(HttpStatusCode.NotFound);
            lock (RowWrites)
                RowWrites.Add((method, path, body));
            if (FailRowWrites)
                return Json(new JsonObject { ["error"] = new JsonObject { ["message"] = "injected row failure" } }, HttpStatusCode.InternalServerError);
            foreach (var (k, v) in body!)
                Rows[id][k] = v?.DeepClone();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
        if (method != "GET")
            return null;
        if (path.StartsWith("bf_connections?"))
            return Value(BfName().Match(path) is { Success: true } n
                ? Rows.Values.Where(r => string.Equals((string?)r["bf_name"], n.Groups[1].Value.Replace("''", "'"), StringComparison.OrdinalIgnoreCase))
                : Rows.Values);
        if (path.StartsWith("solutions?") && path.Contains($"'{AdminCheck.CoreSolution}'"))
            return Value(new[] { new JsonObject { ["solutionid"] = CoreSolutionId } });
        if (path.StartsWith("solutioncomponents?") && path.Contains(CoreSolutionId))
            return Value(new[] { new JsonObject { ["objectid"] = AdminRoleId } });
        if (path.StartsWith("roles?") && path.Contains(AdminCheck.RoleName))
            return Value(_namedRoles.Select(n => new JsonObject { ["roleid"] = n.Id }));
        if (path.StartsWith("systemusers?") && Oid().Match(path) is { Success: true } o)
            return Value(_users.TryGetValue(o.Groups[1].Value, out var u) && !(path.Contains("isdisabled eq false") && _disabled.Contains(o.Groups[1].Value))
                ? new[] { new JsonObject { ["systemuserid"] = u.UserId } } : Array.Empty<JsonObject>());
        if (Related().Match(path) is { Success: true } rel)
        {
            var owner = rel.Groups[2].Value;
            IEnumerable<(string RoleId, string Root)> Roles(List<(string RoleId, string Root)> list) => list;
            return rel.Groups[3].Value switch
            {
                "systemuserroles_association" => Value(Roles(_users.Values.Single(u => u.UserId == owner).Roles)
                    .Select(x => new JsonObject { ["roleid"] = x.RoleId, ["_parentrootroleid_value"] = x.Root })),
                "teammembership_association" => Value(_users.Values.Single(u => u.UserId == owner).Teams.Select(t => new JsonObject { ["teamid"] = t })),
                _ => Value(_teams[owner].Select(x => new JsonObject { ["roleid"] = x.RoleId, ["_parentrootroleid_value"] = x.Root })),
            };
        }
        return null;
    }

    private static HttpResponseMessage Json(JsonNode node, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Value(IEnumerable<JsonObject> rows) =>
        Json(new JsonObject { ["value"] = new JsonArray(rows.Select(r => (JsonNode)r.DeepClone()).ToArray()) });
}
