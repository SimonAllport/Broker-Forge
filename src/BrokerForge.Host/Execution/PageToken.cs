using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BrokerForge.Contracts;
using BrokerForge.Descriptors.Model;
using BrokerForge.Descriptors.Planning;

namespace BrokerForge.Host.Execution;

/// <summary>
/// The host's page token, carried by Dataverse in the paging cookie (phase 0 findings 4): base64url JSON
/// <c>{ i, t, op, v, s, c, h }</c> = service instance, table, operation, descriptor version, strategy,
/// strategy cursor and a hash of the bound filters and sort, then "." and an HMAC-SHA256 tag under the host's page token key. The cookie is
/// user-visible and editable, so the tag stops a caller steering the host's credentials (a forged link or
/// offset); any mismatch or corruption is refused, never treated as page 1.
/// </summary>
public static class PageToken
{
    private sealed record Payload(string I, string T, string Op, int V, string S, string C, string H);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private const int TagBytes = 16;

    public static string Create(OperationContext op, string strategy, string cursor, QueryPlan plan, byte[] key)
    {
        var d = op.Descriptor;
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Payload(op.Instance.Name, op.Map.Table, d.Operation, d.Version, strategy, cursor, QueryHash(plan)), Json));
        return payload + "." + Base64Url(Tag(payload, key));
    }

    /// <summary>Returns the strategy and cursor, or throws <see cref="UnsupportedQueryException"/>.</summary>
    public static (string Strategy, string Cursor) Parse(string token, OperationContext op, QueryPlan plan, byte[] key, string correlationId)
    {
        var d = op.Descriptor;
        UnsupportedQueryException Reject(string why) => new($"The page token is not valid for operation {d.Operation}: {why}.", correlationId);
        Payload? payload;
        try
        {
            var dot = token.IndexOf('.');
            if (dot <= 0 || !CryptographicOperations.FixedTimeEquals(FromBase64Url(token[(dot + 1)..]), Tag(token[..dot], key)))
                throw Reject("it was not issued by this host or has been altered");
            payload = JsonSerializer.Deserialize<Payload>(FromBase64Url(token[..dot]), Json);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw Reject("it is corrupt");
        }
        if (payload is not { I: not null, T: not null, Op: not null, S: not null, C: not null, H: not null })
            throw Reject("it is incomplete");
        if (payload.I != op.Instance.Name || payload.T != op.Map.Table)
            throw Reject($"it was issued for table {payload.T} on service instance {payload.I}");
        if (payload.Op != d.Operation)
            throw Reject($"it was issued by operation {payload.Op}");
        if (payload.V != d.Version)
            throw Reject($"it was issued by descriptor version {payload.V}; the current version is {d.Version}");
        if (payload.H != QueryHash(plan))
            throw Reject("the filters or sort differ from the query that issued it");
        return (payload.S, payload.C);
    }

    private static string QueryHash(QueryPlan plan)
    {
        var text = new StringBuilder();
        foreach (var (param, filter) in plan.ByParam.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            text.Append(param).Append('\u0001').Append(filter.Op).Append('\u0001').AppendJoin('\u0002', filter.SourceValues).Append('\u0003');
        if (plan.Sort is { } sort)
            text.Append("sort\u0001").Append(sort.SourceField).Append(sort.Desc ? "\u0001desc" : "");
        return Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))[..12]);
    }

    private static byte[] Tag(string payload, byte[] key) => HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(payload))[..TagBytes];

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        if (string.IsNullOrEmpty(text))
            throw new FormatException("empty token");
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
