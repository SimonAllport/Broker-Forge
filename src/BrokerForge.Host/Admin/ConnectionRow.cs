using System.Text.Json.Nodes;

namespace BrokerForge.Host.Admin;

/// <summary>
/// One <c>bf_connection</c> row as the admin typed it (spec 5, screen 1). It holds no secret: keys are set on the host's
/// secret page. <see cref="AuthMode"/> is the design's mode name; <see cref="OAuthScopes"/> is space-separated.
/// </summary>
public sealed record ConnectionRow(Guid Id, string Name, string? BaseUrl, string AuthMode, string? StaticKind, string? HeaderName, string? QueryParam,
    string? Username, string? Scope, string? SsoKind, string? AuthorizeUrl, string? TokenUrl, string? OAuthScopes, string? ClientId, bool UsesConnectApp,
    string Prefix, string? Solution, string? OpenApi, string? AnswersJson)
{
    /// <summary>The Web API columns this record reads.</summary>
    public const string Select = "bf_connectionid,bf_name,bf_baseurl,bf_authmode,bf_statickind,bf_headername,bf_queryparam,bf_username,bf_scope,bf_ssokind,"
        + "bf_authorizeurl,bf_tokenurl,bf_oauthscopes,bf_clientid,bf_usesconnectapp,bf_prefix,bf_solution,bf_openapi,bf_answers";

    /// <summary><c>bf_authmode</c> choice values, with the bf publisher's option prefix 10001 (tools/dev/core_admin.py).</summary>
    public static readonly IReadOnlyDictionary<int, string> Modes = new Dictionary<int, string>
    {
        [100010000] = "ServiceAccount", [100010001] = "Static", [100010002] = "OAuth", [100010003] = "Impersonate", [100010004] = "Sso",
    };

    public static ConnectionRow FromJson(JsonObject row)
    {
        string? Text(string column) => (string?)row[column] is { } s && s.Trim().Length > 0 ? s.Trim() : null;
        var mode = (int?)row["bf_authmode"] is { } m && Modes.TryGetValue(m, out var name) ? name : "";
        return new ConnectionRow(Guid.Parse((string)row["bf_connectionid"]!), Text("bf_name") ?? "", Text("bf_baseurl"), mode, Text("bf_statickind"),
            Text("bf_headername"), Text("bf_queryparam"), Text("bf_username"), Text("bf_scope"), Text("bf_ssokind"), Text("bf_authorizeurl"),
            Text("bf_tokenurl"), Text("bf_oauthscopes"), Text("bf_clientid"), (bool?)row["bf_usesconnectapp"] ?? false, Text("bf_prefix") ?? "",
            Text("bf_solution"), (string?)row["bf_openapi"], (string?)row["bf_answers"]);
    }
}
