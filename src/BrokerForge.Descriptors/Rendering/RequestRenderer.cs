using System.Globalization;
using System.Net;
using System.Security;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BrokerForge.Abstractions;
using BrokerForge.Contracts;
using BrokerForge.Descriptors.Model;

namespace BrokerForge.Descriptors.Rendering;

/// <summary>
/// Turns a request spec and a binding context into a <see cref="RenderedRequest"/> (descriptor spec 3-4).
/// Values come only from the binding sources; a missing required value, an invalid format or a header
/// injection attempt fails the request instead of sending something approximate.
/// </summary>
public static partial class RequestRenderer
{
    private enum Kind { String, Number, Bool }

    // Resolved binding values.
    private abstract record V;
    private sealed record OmitV : V { public static readonly OmitV I = new(); }
    private sealed record NullV : V { public static readonly NullV I = new(); }
    private sealed record TextV(string Value, Kind Kind) : V;
    private sealed record ManyV(IReadOnlyList<string> Values) : V;
    private sealed record JsonV(JsonNode? Node) : V;

    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex PathPlaceholder();

    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?$")]
    private static partial Regex JsonNumber();

    public static async Task<RenderedRequest> RenderAsync(RequestSpec spec, OperationDescriptor d, BindingContext ctx, ISecretResolver secrets, CancellationToken ct)
    {
        var r = new Run(d, ctx, secrets, ct);

        var path = spec.Path;
        foreach (var (name, binding) in spec.PathParams)
        {
            var text = ToText(await r.Resolve(binding, $"request.pathParams.{name}"))
                ?? throw r.Validation($"path parameter `{name}` has no value");
            // "." and ".." survive escaping and would move the request to another route once normalised.
            if (text is "." or "..")
                throw r.Validation($"path parameter `{name}` must not be a dot segment");
            path = path.Replace("{" + name + "}", Uri.EscapeDataString(text), StringComparison.Ordinal);
        }

        var query = new List<string>();
        foreach (var (name, binding) in spec.Query)
        {
            var value = await r.Resolve(binding, $"request.query.{name}");
            if (binding is SecretBinding)
                r.SecretParams.Add(name);
            var text = ToText(value);
            if (value is OmitV || (text == null && binding.Modifiers.OmitIfNull != false))
                continue;
            query.Add(Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(text ?? ""));
        }
        if (ReferenceEquals(spec, d.Request))
            AddPagingParams(d.Paging, ctx.Paging, query);

        var headers = new List<KeyValuePair<string, string>>();
        foreach (var (name, binding) in spec.Headers)
        {
            var value = await r.Resolve(binding, $"request.headers.{name}");
            var text = ToText(value);
            if (value is OmitV || (text == null && binding.Modifiers.OmitIfNull != false))
                continue;
            if (text != null && (text.Contains('\r') || text.Contains('\n')))
                throw r.Validation($"header `{name}` value contains a line break");
            headers.Add(new(name, text ?? ""));
        }

        byte[]? body = null;
        if (spec.Body is { } b)
            body = b.Template != null ? await RenderTemplate(r, b) : Encoding.UTF8.GetBytes(await RenderRaw(r, b));

        var pathAndQuery = query.Count == 0 ? path : path + (path.Contains('?') ? "&" : "?") + string.Join("&", query);
        return new RenderedRequest(ctx.Instance.BaseUrl, spec.Method, pathAndQuery, headers, body, spec.Body?.ContentType, r.SecretParams, r.SecretValues);
    }

    /// <summary>Every column a request's col bindings read, across path, query, headers and body.</summary>
    public static IReadOnlySet<string> BoundColumns(RequestSpec spec)
    {
        var columns = new HashSet<string>(StringComparer.Ordinal);
        void Add(Binding b) { if (b is ColBinding c) columns.Add(c.Column); }
        void Walk(BodyNode? node)
        {
            switch (node)
            {
                case BodyObject o: foreach (var m in o.Members) Walk(m.Value); break;
                case BodyArray a: foreach (var i in a.Items) Walk(i); break;
                case BodyLeaf l: Add(l.Binding); break;
            }
        }
        foreach (var b in spec.PathParams.Values.Concat(spec.Query.Select(q => q.Value)).Concat(spec.Headers.Select(h => h.Value)))
            Add(b);
        Walk(spec.Body?.Template);
        foreach (var p in spec.Body?.RawPlaceholders ?? Array.Empty<RawPlaceholder>())
            Add(p.Binding);
        return columns;
    }

    private static void AddPagingParams(PagingSpec? paging, PagingValues? values, List<string> query)
    {
        if (paging == null || values == null)
            return;
        void Add(string name, string value) => query.Add(Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value));
        if (paging.PageSize is { } ps)
            Add(ps.Param, values.PageSize.ToString(CultureInfo.InvariantCulture));
        if (paging.Strategy == "cursor" && paging.Token is { } t && values.Token != null)
            Add(t.Param, values.Token);
        if (paging.Strategy == "offset" && paging.Offset is { } o && values.Offset is { } offset)
            Add(o.Param, offset.ToString(CultureInfo.InvariantCulture));
        if (paging.Strategy == "pageNumber" && paging.Page is { } p && values.PageNumber is { } number)
            Add(p.Param, number.ToString(CultureInfo.InvariantCulture));
    }

    private static async Task<byte[]> RenderTemplate(Run r, BodySpec b)
    {
        switch (b.ContentType)
        {
            case BodySpec.Json:
                var (node, _) = await BuildJson(r, b.Template!, "request.body.template", root: true);
                return Encoding.UTF8.GetBytes(node?.ToJsonString() ?? "null");
            case BodySpec.Form:
                if (b.Template is not BodyObject form || form.Members.Any(m => m.Value is not BodyLeaf))
                    throw new ConfigurationException($"{r.D.Source}: a form body template must be a flat mapping of fields");
                var pairs = new List<string>();
                foreach (var (name, leaf) in form.Members)
                {
                    var binding = ((BodyLeaf)leaf).Binding;
                    var value = await r.Resolve(binding, $"request.body.template.{name}");
                    var text = ToText(value);
                    if (value is OmitV || (text == null && binding.Modifiers.OmitIfNull != false))
                        continue;
                    pairs.Add(WebUtility.UrlEncode(name) + "=" + WebUtility.UrlEncode(text ?? ""));
                }
                return Encoding.UTF8.GetBytes(string.Join("&", pairs));
            default:
                throw new ConfigurationException($"{r.D.Source}: {b.ContentType} bodies use `raw`, not `template`");
        }
    }

    /// <summary>Builds the JSON body. Omitted leaves drop their key; an object left empty by omissions is dropped too.</summary>
    private static async Task<(JsonNode? Node, bool Omitted)> BuildJson(Run r, BodyNode node, string path, bool root = false)
    {
        switch (node)
        {
            case BodyObject obj:
                var result = new JsonObject();
                foreach (var (name, child) in obj.Members)
                {
                    var (value, omitted) = await BuildJson(r, child, $"{path}.{name}");
                    if (!omitted)
                        result[name] = value;
                }
                return (result, !root && obj.Members.Count > 0 && result.Count == 0);
            case BodyArray arr:
                var array = new JsonArray();
                for (var i = 0; i < arr.Items.Count; i++)
                {
                    var (value, omitted) = await BuildJson(r, arr.Items[i], $"{path}[{i}]");
                    if (!omitted)
                        array.Add(value);
                }
                return (array, false);
            case BodyLeaf leaf:
                var v = await r.Resolve(leaf.Binding, path);
                if (v is OmitV || (v is NullV && leaf.Binding.Modifiers.OmitIfNull == true))
                    return (null, true);
                return (ToJson(v), false);
            default:
                throw new InvalidOperationException($"unknown body node at {path}");
        }
    }

    private static async Task<string> RenderRaw(Run r, BodySpec b)
    {
        var text = b.Raw!;
        foreach (var placeholder in b.RawPlaceholders)
        {
            var value = ToText(await r.Resolve(placeholder.Binding, "request.body.raw")) ?? "";
            var escaped = b.ContentType switch
            {
                BodySpec.Xml => SecurityElement.Escape(value),
                BodySpec.Form => WebUtility.UrlEncode(value),
                _ => JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString(),
            };
            text = text.Replace(placeholder.Token, escaped, StringComparison.Ordinal);
        }
        return text;
    }

    private static string? ToText(V v) => v switch
    {
        TextV t => t.Value,
        ManyV m => string.Join(",", m.Values),
        JsonV j => j.Node?.ToJsonString(),
        _ => null,
    };

    private static JsonNode? ToJson(V v) => v switch
    {
        TextV { Kind: Kind.Number } t => JsonNode.Parse(t.Value),
        TextV { Kind: Kind.Bool } t => JsonValue.Create(t.Value == "true"),
        TextV t => JsonValue.Create(t.Value),
        ManyV m => new JsonArray(m.Values.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
        JsonV j => j.Node?.DeepClone(),
        _ => null,
    };

    private sealed class Run(OperationDescriptor d, BindingContext ctx, ISecretResolver secrets, CancellationToken ct)
    {
        public OperationDescriptor D => d;
        public HashSet<string> SecretParams { get; } = new(StringComparer.Ordinal);
        public List<string> SecretValues { get; } = new();

        public MappedSourceException Validation(string message) =>
            new(SourceErrorKinds.Validation, $"{message} (operation {d.Operation}).", ctx.CorrelationId);

        public async Task<V> Resolve(Binding binding, string path)
        {
            var value = await Source(binding, path);
            if (value is NullV && binding.Modifiers.Required == true)
                throw binding is FilterBinding f
                    ? new UnsupportedQueryException($"The source requires a filter on column {f.Column} (operation {d.Operation}).", ctx.CorrelationId)
                    : Validation($"{Describe(binding)} is required");
            return binding.Modifiers.Format is { } format ? ApplyFormat(value, format, binding, path) : value;
        }

        private async Task<V> Source(Binding binding, string path)
        {
            switch (binding)
            {
                case LiteralBinding l: return Scalar(l.Value, l.Quoted);
                case StaticBinding s: return Scalar(s.Value, s.Quoted);
                case InstanceBinding i:
                    return ctx.Instance.Config.TryGetValue(i.Key, out var config)
                        ? new TextV(config, Kind.String)
                        : throw new ConfigurationException($"Service instance {ctx.Instance.Name} has no config value `{i.Key}` ({d.Source} {path}).");
                case SecretBinding s:
                    if (!ctx.Instance.Secrets.TryGetValue(s.Name, out var reference))
                        throw new ConfigurationException($"Service instance {ctx.Instance.Name} has no secret reference named `{s.Name}` ({d.Source} {path}).");
                    var secret = await secrets.GetAsync(SecretRef.Parse(reference), ctx.Instance, ct);
                    SecretValues.Add(secret);
                    SecretValues.Add(Uri.EscapeDataString(secret));
                    return new TextV(secret, Kind.String);
                case ContextBinding c:
                    return c.Name switch
                    {
                        "userObjectId" => ctx.UserObjectId == null ? NullV.I : new TextV(ctx.UserObjectId, Kind.String),
                        "correlationId" => new TextV(ctx.CorrelationId, Kind.String),
                        "environmentId" => new TextV(ctx.EnvironmentId, Kind.String),
                        "async" => new TextV(ctx.Async ? "true" : "false", Kind.Bool),
                        _ => throw new InvalidOperationException($"unknown context `{c.Name}`"),
                    };
                case ComputedBinding c:
                    return c.Kind switch
                    {
                        "uuid" => new TextV(Guid.NewGuid().ToString("D"), Kind.String),
                        "utcnow" => new TextV(DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture), Kind.String),
                        _ => throw new InvalidOperationException($"unknown computed `{c.Kind}`"),
                    };
                case ColBinding c:
                    if (d.Binds == Messages.Update && ctx.ChangedColumns?.Contains(c.Column) != true)
                    {
                        // The platform passes only the columns the update carries, so an unchanged value is unknown.
                        if (c.AlwaysSend)
                            throw Validation($"column {c.Column} must be supplied on every update (alwaysSend)");
                        return OmitV.I;
                    }
                    if (ctx.Record == null || !ctx.Record.TryGetValue(c.Column, out var cell) || cell.Value == null)
                        return NullV.I;
                    // The Dataverse integer is not the source's value: only a declared map can turn it back.
                    if (cell.Type == CellTypes.OptionSet)
                        return c.OptionSet is { } setName
                            ? new TextV(SourceOption(setName, c.Column, cell.Value), Kind.String)
                            : throw new UnsupportedQueryException($"Column {c.Column} is an option set; bind it with `optionset: <map>` so the value can be mapped back to the source's (operation {d.Operation}).", ctx.CorrelationId);
                    return Typed(cell);
                case ParamBinding p:
                    if (ctx.Inputs == null || !ctx.Inputs.TryGetValue(p.Name, out var input) || input.Value == null)
                        return NullV.I;
                    return Typed(input);
                case IdBinding id:
                    if (id.Part != null)
                        throw new ConfigurationException($"{d.Source}: composite key parts are not supported until the key map (phase 5)");
                    if (ctx.Id == null)
                        throw Validation("the record id is required");
                    return new TextV(ctx.Id.Value, ctx.Id.Kind is Keys.KeyKind.Int32 or Keys.KeyKind.Int64 ? Kind.Number : Kind.String);
                case FilterBinding:
                    var param = path[(path.LastIndexOf('.') + 1)..];
                    if (ctx.Plan?.ByParam.GetValueOrDefault(param) is not { } bound)
                        return NullV.I;
                    return bound.Op == Ops.In ? new ManyV(bound.SourceValues) : new TextV(bound.SourceValues[0], Kind.String);
                case SortBinding s:
                    if (ctx.Plan?.Sort is not { } sort)
                        return NullV.I;
                    return new TextV(sort.Desc ? s.DescFormat!.Replace("{field}", sort.SourceField, StringComparison.Ordinal) : sort.SourceField, Kind.String);
                case PagingBinding p:
                    var pv = ctx.Paging;
                    string? text = p.Kind switch
                    {
                        "pageSize" => pv?.PageSize.ToString(CultureInfo.InvariantCulture),
                        "token" => pv?.Token,
                        "offset" => pv?.Offset?.ToString(CultureInfo.InvariantCulture),
                        "pageNumber" => pv?.PageNumber?.ToString(CultureInfo.InvariantCulture),
                        _ => null,
                    };
                    return text == null ? NullV.I : new TextV(text, Kind.String);
                default:
                    throw new InvalidOperationException($"unhandled binding {binding.GetType().Name} at {path}");
            }
        }

        private static TextV Typed(Cell cell) => new(cell.Value, cell.Type switch
        {
            CellTypes.Int or CellTypes.Decimal when JsonNumber().IsMatch(cell.Value) => Kind.Number,
            CellTypes.Bool => Kind.Bool,
            _ => Kind.String,
        });

        private string SourceOption(string setName, string column, string optionValue)
        {
            foreach (var (sourceValue, option) in d.Optionsets[setName].Values)
                if (option.ToString(CultureInfo.InvariantCulture) == optionValue)
                    return sourceValue;
            throw Validation($"column {column}: option value {optionValue} has no entry in option set {setName}");
        }

        private static V Scalar(string value, bool quoted)
        {
            if (quoted)
                return new TextV(value, Kind.String);
            return value switch
            {
                "" or "null" or "~" => NullV.I,
                "true" or "false" => new TextV(value, Kind.Bool),
                _ when JsonNumber().IsMatch(value) => new TextV(value, Kind.Number),
                _ => new TextV(value, Kind.String),
            };
        }

        private V ApplyFormat(V value, string format, Binding binding, string path)
        {
            if (value is OmitV or NullV)
                return value;
            if (format == "csv")
                return value is ManyV m ? new TextV(string.Join(",", m.Values), Kind.String) : value;
            var text = ToText(value)!;
            switch (format)
            {
                case "json":
                    try { return new JsonV(JsonNode.Parse(text)); }
                    catch (JsonException) { throw Mapping($"{Describe(binding)} is not valid JSON ({path})"); }
                case "lower": return new TextV(text.ToLowerInvariant(), Kind.String);
                case "upper": return new TextV(text.ToUpperInvariant(), Kind.String);
            }
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
                throw Mapping($"{Describe(binding)} is not a date and time, so format {format} cannot apply ({path})");
            var utc = at.ToUniversalTime();
            return format switch
            {
                "iso8601" => new TextV(utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture), Kind.String),
                "date" => new TextV(utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Kind.String),
                "epochSeconds" => new TextV(utc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), Kind.Number),
                "epochMillis" => new TextV(utc.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), Kind.Number),
                _ => throw new InvalidOperationException($"unknown format {format}"),
            };
        }

        private MappingException Mapping(string message) => new($"{message} (operation {d.Operation}).", ctx.CorrelationId);

        private static string Describe(Binding binding) => binding switch
        {
            ColBinding c => $"column {c.Column}",
            InstanceBinding i => $"instance value {i.Key}",
            ContextBinding c => $"context {c.Name}",
            ParamBinding p => $"parameter {p.Name}",
            _ => binding.GetType().Name.Replace("Binding", "").ToLowerInvariant() + " value",
        };
    }
}
