using System.Text.Json;
using BrokerForge.Connections;
using BrokerForge.Contracts;
using BrokerForge.Descriptors.Parsing;
using BrokerForge.SwaggerForge;

namespace BrokerForge.Host.Admin;

/// <summary>
/// Turns a <c>bf_connection</c> row into the review the admin sees and, once nothing is open, the connection design
/// Generate publishes (spec 6 steps 1–3). Every value comes from the row or the draft service; nothing is guessed.
/// </summary>
public static class DesignBuilder
{
    public const string BrokerType = "rest";

    /// <summary>The admin's answers (<c>bf_answers</c>): a JSON object of strings, or nothing.</summary>
    public static IReadOnlyDictionary<string, string> Answers(ConnectionRow row, string correlationId = "")
    {
        if (string.IsNullOrWhiteSpace(row.AnswersJson))
            return new Dictionary<string, string>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(row.AnswersJson)
                ?? throw new JsonException("null");
        }
        catch (JsonException)
        {
            throw new MappedSourceException(SourceErrorKinds.Validation,
                $"Connection {row.Name}: bf_answers must be a JSON object of strings; save the answers again from the review page.", correlationId);
        }
    }

    /// <summary>The draft service's review of the row's definition with its answers. Never throws for pasted text.</summary>
    public static DraftReview Review(ConnectionRow row, string correlationId = "") =>
        DraftService.Review(row.OpenApi, new DraftOptions(row.Name, row.Prefix, ReservedNames(row)), Answers(row, correlationId));

    /// <summary>The design of a ready review. The version is left at 0: publishing sets it (<c>DesignPublisher</c>).</summary>
    public static ConnectionDesign Build(ConnectionRow row, DraftReview review, string correlationId = "")
    {
        if (review.Result is not { } result)
            throw new MappedSourceException(SourceErrorKinds.Validation, $"Connection {row.Name} is not ready to generate: {Open(review)}.", correlationId);

        DesignObjectMap? map = null;
        var bound = result.Descriptors.Select(d => DescriptorParser.Parse(d.Value, d.Key)).Where(d => d.CustomApi == null).ToList();
        if (bound.Count > 0)
        {
            var kind = review.Table?.KeyKind
                ?? throw new MappedSourceException(SourceErrorKinds.Validation, $"Connection {row.Name}: the table's key is not known.", correlationId);
            map = new DesignObjectMap(review.Table.LogicalName, kind == "Guid" ? "native" : "reversible", kind,
                bound.ToDictionary(d => d.Binds, d => d.Operation, StringComparer.Ordinal));
        }

        return new ConnectionDesign(1, row.Name, 0, BrokerType, row.OpenApi ?? "", Answers(row, correlationId), Auth(row), map, result.SchemaYaml,
            result.Descriptors.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
    }

    /// <summary>The source's solution: <c>bf_solution</c>, else <c>BrokerForge&lt;Instance&gt;</c>.</summary>
    public static string SolutionOf(ConnectionRow row) => row.Solution ?? $"BrokerForge{row.Name}";

    /// <summary>The publisher for the table prefix: <c>brokerforge&lt;prefix&gt;</c>, as dev uses <c>brokerforgeext</c>.</summary>
    public static string PublisherOf(ConnectionRow row) => $"brokerforge{row.Prefix}";

    /// <summary>
    /// Static mode uses one instance secret, <c>key</c> (API key or basic password); OAuth with the source's own client
    /// uses <c>clientSecret</c>; the other modes use none. Names only: the values are set on the host's secret page.
    /// Impersonate is the Entra flow through the connect app, so it always has <c>auth.oauth</c> with the connect app and
    /// the scopes in <c>bf_oauthscopes</c>, whatever the row's own URLs, client ID or connect-app flag say.
    /// </summary>
    public static DesignAuth Auth(ConnectionRow row)
    {
        var connectApp = row.AuthMode == "Impersonate" || row.UsesConnectApp;
        var oauth = row.AuthMode is "OAuth" or "Impersonate"
            ? new DesignOAuth(connectApp ? null : row.AuthorizeUrl, connectApp ? null : row.TokenUrl,
                (row.OAuthScopes ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries), connectApp ? null : row.ClientId, connectApp)
            : null;
        IReadOnlyList<string> secrets = row.AuthMode switch
        {
            "Static" => new[] { "key" },
            "OAuth" when !row.UsesConnectApp => new[] { "clientSecret" },
            _ => Array.Empty<string>(),
        };
        return new DesignAuth(row.AuthMode, row.StaticKind, row.HeaderName, row.QueryParam, row.Username, row.Scope, row.SsoKind, null, oauth, secrets);
    }

    /// <summary>The header and query names the authentication sends, which the definition's parameters must leave to it.</summary>
    private static IReadOnlyList<string> ReservedNames(ConnectionRow row) =>
        new[] { row.HeaderName, row.QueryParam }.OfType<string>().ToList();

    private static string Open(DraftReview review)
    {
        var open = (review.Table?.Questions ?? Array.Empty<Question>()).Concat(review.Endpoints.SelectMany(e => e.Questions))
            .Where(q => q.Open).Select(q => q.Key).Distinct().ToList();
        var parts = new List<string>();
        if (open.Count > 0)
            parts.Add("open questions " + string.Join(", ", open));
        parts.AddRange(review.Problems);
        parts.AddRange(review.Endpoints.Where(e => e.Status == EndpointStatus.Invalid).Select(e => $"{e.Operation} is invalid: {string.Join("; ", e.Problems)}"));
        if (parts.Count == 0)
            parts.Add("no endpoint is in use");
        return string.Join("; ", parts);
    }
}
