using System.Globalization;
using System.Text.RegularExpressions;
using BrokerForge.Abstractions;
using BrokerForge.Contracts;
using BrokerForge.Descriptors.Mapping;
using BrokerForge.Descriptors.Model;
using BrokerForge.Descriptors.Planning;
using BrokerForge.Descriptors.Rendering;

namespace BrokerForge.Host.Execution;

/// <summary>
/// Reads one Dataverse page (descriptor spec 7). It fills the requested page with up to
/// paging.maxCallsPerPage source calls; when the budget runs out first it returns the rows collected
/// with a next token (D-12). Every call shares the request deadline. Pages are never padded, trimmed
/// or filtered client-side. <paramref name="tokenKey"/> signs the page tokens it hands out.
/// </summary>
public sealed partial class PagingCoordinator(OperationExecutor executor, ISecretResolver secrets, byte[] tokenKey)
{
    [GeneratedRegex(@"<([^>]+)>\s*;\s*rel=""?next""?", RegexOptions.IgnoreCase)]
    private static partial Regex NextLink();

    public async Task<(IReadOnlyList<MappedRow> Rows, string? Next)> ReadPageAsync(OperationContext op, QueryPlan plan, Deadline deadline, CancellationToken ct)
    {
        var d = op.Descriptor;
        var paging = d.Paging ?? throw new ConfigurationException($"{d.Source}: RetrieveMultiple has no paging block");
        var correlationId = op.Binding.CorrelationId;
        var strategy = paging.Strategy;
        var target = plan.RequestedSize;
        UnsupportedQueryException Reject(string why) => new($"{why} (operation {d.Operation}).", correlationId);

        // Strategy state. The token, when present, has already been checked against the operation, version and query.
        string? cursor = null, link = null;
        var offset = 0;
        var pageNumber = paging.Page?.Base ?? 1;
        var fixedSize = Math.Min(plan.PageSize, target);
        if (plan.Token != null)
        {
            var (tokenStrategy, c) = PageToken.Parse(plan.Token, op, plan, tokenKey, correlationId);
            if (strategy == "none")
                throw Reject("This source returns everything in one page; there is no next page to request");
            if (tokenStrategy != strategy)
                throw Reject($"The page token is for {tokenStrategy} paging, but the operation uses {strategy}");
            switch (strategy)
            {
                case "cursor": cursor = c; break;
                case "offset":
                    offset = int.TryParse(c, NumberStyles.None, CultureInfo.InvariantCulture, out var o) ? o : throw Reject("The page token's offset is corrupt");
                    break;
                case "pageNumber":
                    var parts = c.Split(':');
                    if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out pageNumber)
                        || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out fixedSize) || fixedSize < 1)
                        throw Reject("The page token's page position is corrupt");
                    break;
                case "link":
                    var space = c.IndexOf(' ');
                    if (space < 1 || !int.TryParse(c[..space], NumberStyles.None, CultureInfo.InvariantCulture, out fixedSize) || fixedSize < 1)
                        throw Reject("The page token's link is corrupt");
                    link = c[(space + 1)..];
                    if (!UnderBase(link, op.Instance.BaseUrl))
                        throw Reject("The page token's link is outside the service instance base URL");
                    break;
            }
        }

        var rows = new List<MappedRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var budget = strategy == "none" ? 1 : paging.MaxCallsPerPage;
        var calls = 0;
        bool more;
        while (true)
        {
            var remaining = target - rows.Count;
            var fixedStep = strategy is "pageNumber" or "link";
            var callSize = fixedStep ? fixedSize : Math.Min(plan.PageSize, remaining);

            var binding = op.Binding with { Plan = plan, Paging = new PagingValues(callSize, cursor, offset, pageNumber) };
            var request = await RequestRenderer.RenderAsync(d.Request, d, binding, secrets, ct);
            if (link != null)
            {
                // A link may change the query (its cursor) but never the operation's path.
                var uri = new Uri(link);
                if (uri.AbsolutePath != RenderedPath(request))
                    throw plan.Token != null && calls == 0
                        ? Reject("The page token's link is not for this operation's path")
                        : new SourceException($"The source's next link is not for this operation's path (operation {d.Operation}).", correlationId);
                // Secret query parameters were stripped before the link went into a token; put the descriptor's back.
                request = request with { BaseUrl = uri.GetLeftPart(UriPartial.Authority), PathAndQuery = WithParams(uri.PathAndQuery, SecretPairs(request)) };
            }

            var result = await executor.ExecuteAsync(d, request, op.Channel, deadline, correlationId, ct);
            ErrorMapper.ThrowIfError(d, result, correlationId, Redact(request, op.Channel));
            var page = ResponseMapper.MapRows(d, result, op.Keys.Kind, correlationId);
            rows.AddRange(page);
            calls++;

            SourceException Source(string why) => new($"{why} (operation {d.Operation}).", correlationId, result.Status);
            if (strategy != "none" && page.Count > callSize)
                throw Source($"The source returned {page.Count} rows for a page size of {callSize}, so it ignored the page size and pages cannot be tracked");
            // Two rows with one key would reach Dataverse as one record twice. Checked within the page only:
            // the host keeps no state between pages.
            if (page.FirstOrDefault(row => !seen.Add(row.Key.Value)) is { } repeated)
                throw Source($"The source returned key {repeated.Key.Value} twice in one page");
            switch (strategy)
            {
                case "cursor":
                    var next = ResponseMapper.SelectScalar(result.Body, paging.Token!.Next, m => Source(m));
                    more = !string.IsNullOrEmpty(next);
                    if (more && next == cursor)
                        throw Source("The source returned the same cursor again, so paging would never end");
                    cursor = next;
                    break;
                case "offset":
                    offset += page.Count;
                    if (paging.Offset!.Total is { } totalPath)
                    {
                        var total = ResponseMapper.SelectScalar(result.Body, totalPath, m => Source(m));
                        more = int.TryParse(total, NumberStyles.None, CultureInfo.InvariantCulture, out var t)
                            ? offset < t
                            : throw Source($"The total at {totalPath} is not a whole number");
                        if (more && page.Count == 0)
                            throw Source($"The source's total at {totalPath} is {t} but it returned no rows at offset {offset}");
                    }
                    else
                    {
                        more = page.Count == callSize;
                    }
                    more &= page.Count > 0;
                    break;
                case "pageNumber":
                    more = page.Count == callSize;
                    pageNumber++;
                    break;
                case "link":
                    link = NextFrom(result, request, op.Instance.BaseUrl, Source);
                    if (link != null)
                        link = WithoutParams(link, SecretParamNames(request, op.Instance));
                    more = link != null;
                    break;
                default:
                    more = false;
                    break;
            }

            if (!more || rows.Count >= target || calls >= budget)
                break;
            // A fixed-size call that would overflow the page waits for the next Dataverse page instead.
            if (fixedStep && fixedSize > target - rows.Count)
                break;
        }

        if (!more)
            return (rows, null);
        var state = strategy switch
        {
            "cursor" => cursor!,
            "offset" => offset.ToString(CultureInfo.InvariantCulture),
            "pageNumber" => $"{pageNumber}:{fixedSize}",
            "link" => $"{fixedSize} {link}",
            _ => throw new InvalidOperationException(strategy),
        };
        return (rows, PageToken.Create(op, strategy, state, plan, tokenKey));
    }

    private static string? NextFrom(BrokerResult result, RenderedRequest request, string baseUrl, Func<string, SourceException> fail)
    {
        if (!result.Headers.TryGetValue("Link", out var values))
            return null;
        foreach (var value in values)
            if (NextLink().Match(value) is { Success: true } m)
            {
                var url = new Uri(new Uri(request.BaseUrl.TrimEnd('/') + request.PathAndQuery), m.Groups[1].Value).AbsoluteUri;
                return UnderBase(url, baseUrl) ? url : throw fail("The source's next link points outside the service instance base URL");
            }
        return null;
    }

    private static string RenderedPath(RenderedRequest request) =>
        new Uri(request.BaseUrl.TrimEnd('/') + request.PathAndQuery).AbsolutePath;

    /// <summary>True when <paramref name="url"/> has the base URL's scheme, host and port, no user info, and a path under the base path.</summary>
    public static bool UnderBase(string url, string baseUrl)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var b))
            return false;
        var basePath = b.AbsolutePath.TrimEnd('/') + "/";
        return u.Scheme == b.Scheme
            && string.Equals(u.Host, b.Host, StringComparison.OrdinalIgnoreCase)
            && u.Port == b.Port
            && string.IsNullOrEmpty(u.UserInfo)
            && (u.AbsolutePath + "/").StartsWith(basePath, StringComparison.Ordinal);
    }

    /// <summary>Every secret value a source reply could echo back: rendered secrets and the channel's credentials.</summary>
    public static IEnumerable<string> Redact(RenderedRequest request, IAuthenticatedChannel? channel) =>
        request.SecretValues.Concat(channel?.CredentialValues ?? Array.Empty<string>());

    private static HashSet<string> SecretParamNames(RenderedRequest request, ServiceInstance instance)
    {
        var names = new HashSet<string>(request.SecretQueryParams, StringComparer.Ordinal);
        if (instance.Auth is { StaticKind: "apiKeyQuery", QueryParam: { } p })
            names.Add(p);
        return names;
    }

    private static IEnumerable<string> QueryPairs(string pathAndQuery) =>
        pathAndQuery.IndexOf('?') is var q and >= 0 ? pathAndQuery[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();

    private static string ParamName(string pair) => Uri.UnescapeDataString(pair.IndexOf('=') is var eq and > 0 ? pair[..eq] : pair);

    private static List<string> SecretPairs(RenderedRequest request) =>
        QueryPairs(request.PathAndQuery).Where(p => request.SecretQueryParams.Contains(ParamName(p))).ToList();

    private static string WithoutParams(string url, HashSet<string> names)
    {
        var uri = new Uri(url);
        var kept = QueryPairs(uri.PathAndQuery).Where(p => !names.Contains(ParamName(p))).ToList();
        return uri.GetLeftPart(UriPartial.Path) + (kept.Count == 0 ? "" : "?" + string.Join("&", kept));
    }

    private static string WithParams(string pathAndQuery, List<string> pairs) =>
        pairs.Count == 0 ? pathAndQuery : pathAndQuery + (pathAndQuery.Contains('?') ? "&" : "?") + string.Join("&", pairs);
}
