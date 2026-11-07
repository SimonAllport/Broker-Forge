using System.Globalization;
using System.Text.RegularExpressions;
using BrokerForge.Contracts;
using BrokerForge.Descriptors.Model;
using Json.Path;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace BrokerForge.Descriptors.Parsing;

/// <summary>
/// Strict descriptor parser (descriptor spec 1-8). Walks the YAML representation model so every
/// error names the YAML path. Unknown keys are errors; features not yet delivered are rejected by name.
/// Structural rules that need the whole descriptor are in <see cref="Validation.DescriptorValidator"/>.
/// </summary>
public static partial class DescriptorParser
{
    public static OperationDescriptor Parse(string yaml, string source) => new Reader(source).Read(yaml);

    private sealed partial class Reader(string source)
    {
        private static readonly string[] SourceKeys = { "static", "instance", "secret", "context", "col", "id", "filter", "sort", "paging", "computed", "param", "step" };
        private static readonly string[] ModifierKeys = { "required", "omitIfNull", "format" };
        private static readonly Dictionary<string, string[]> SourceExtras = new()
        {
            ["filter"] = new[] { "ops" },
            ["sort"] = new[] { "descFormat" },
            ["paging"] = new[] { "max" },
            ["col"] = new[] { "alwaysSend", "optionset" },
        };
        private static readonly HashSet<string> Methods = new() { "GET", "POST", "PUT", "PATCH", "DELETE" };
        private static readonly HashSet<string> OpNames = new() { Ops.Eq, Ops.Ne, Ops.Gt, Ops.Ge, Ops.Lt, Ops.Le, Ops.In, Ops.Like };
        private static readonly HashSet<string> CrudMessages = new() { Messages.Retrieve, Messages.RetrieveMultiple, Messages.Create, Messages.Update, Messages.Delete };

        [GeneratedRegex(@"\{\{\s*([A-Za-z]+)(?:\.([A-Za-z0-9_]+))?\s*\}\}")]
        private static partial Regex RawPlaceholderRegex();

        [GeneratedRegex(@"^[a-z][A-Za-z0-9]*$")]
        private static partial Regex CamelCaseRegex();

        [GeneratedRegex(@"^[a-z][a-z0-9]*_[A-Za-z][A-Za-z0-9_]*$")]
        private static partial Regex UniqueNameRegex();

        [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*$")]
        private static partial Regex ParamNameRegex();

        private bool _customApi;

        private DescriptorException Fail(string path, string message) => new(source, path, message);

        private static string Join(string path, string key) => path.Length == 0 ? key : path + "." + key;

        public OperationDescriptor Read(string yaml)
        {
            var stream = new YamlStream();
            try
            {
                stream.Load(new StringReader(yaml));
            }
            catch (YamlException ex)
            {
                throw Fail("", $"invalid YAML at line {ex.Start.Line}: {ex.Message}");
            }
            if (stream.Documents.Count != 1)
                throw Fail("", "a descriptor file holds exactly one YAML document");

            var root = Mapping(stream.Documents[0].RootNode, "");
            foreach (var (key, phase) in new[] { ("steps", "a later phase"), ("hook", "a later phase"), ("cache", "phase 5") })
                if (Get(root, key) != null)
                    throw Fail(key, $"`{key}` is not supported until {phase}");
            OnlyKeys(root, "", "operation", "version", "binds", "table", "request", "response", "paging", "errors", "retry", "optionsets", "filters", "sortStable");

            var operation = RequiredScalar(root, "", "operation");
            if (!CamelCaseRegex().IsMatch(operation))
                throw Fail("operation", $"operation `{operation}` must be camelCase");
            var version = Int(Require(root, "", "version"), "version");
            if (version < 1)
                throw Fail("version", "version must be 1 or more");
            var binds = RequiredScalar(root, "", "binds");
            if (binds.StartsWith(OperationDescriptor.CustomApiPrefix, StringComparison.Ordinal))
            {
                var unique = binds[OperationDescriptor.CustomApiPrefix.Length..];
                if (!UniqueNameRegex().IsMatch(unique))
                    throw Fail("binds", $"`{unique}` is not a Custom API unique name with a publisher prefix, such as ext_SubmitOrder");
                if (Get(root, "table") != null)
                    throw Fail("table", "a Custom API descriptor has no table; unbound Custom APIs only (HLD D-19)");
                _customApi = true;
            }
            else if (!CrudMessages.Contains(binds))
            {
                throw Fail("binds", $"binds must be one of {string.Join(", ", CrudMessages)} or customapi:<UniqueName>; got `{binds}`");
            }

            return new OperationDescriptor(
                operation,
                version,
                binds,
                OptionalScalar(root, "table"),
                ReadRequest(Mapping(Require(root, "", "request"), "request"), "request"),
                Get(root, "response") is { } response ? ReadResponse(Mapping(response, "response"), "response") : null,
                Get(root, "paging") is { } paging ? ReadPaging(Mapping(paging, "paging"), "paging") : null,
                Get(root, "errors") is { } errors ? ReadErrors(Mapping(errors, "errors"), "errors") : null,
                Get(root, "retry") is { } retry ? ReadRetry(Mapping(retry, "retry"), "retry") : null,
                Get(root, "optionsets") is { } optionsets ? ReadOptionSets(Mapping(optionsets, "optionsets"), "optionsets") : new Dictionary<string, OptionSetMap>(),
                Get(root, "filters") is { } filters ? ReadFilters(Mapping(filters, "filters"), "filters") : FiltersSpec.Default,
                Get(root, "sortStable") is { } sortStable ? Bool(sortStable, "sortStable") : null,
                source);
        }

        private RequestSpec ReadRequest(YamlMappingNode m, string path)
        {
            OnlyKeys(m, path, "method", "path", "pathParams", "query", "headers", "body");
            var method = RequiredScalar(m, path, "method");
            if (!Methods.Contains(method))
                throw Fail(Join(path, "method"), $"method must be one of {string.Join(", ", Methods)}; got `{method}`");
            return new RequestSpec(
                method,
                RequiredScalar(m, path, "path"),
                Named(m, path, "pathParams").ToDictionary(kv => kv.Key, kv => kv.Value),
                Named(m, path, "query"),
                Named(m, path, "headers"),
                Get(m, "body") is { } body ? ReadBody(Mapping(body, Join(path, "body")), Join(path, "body")) : null);
        }

        private List<KeyValuePair<string, Binding>> Named(YamlMappingNode parent, string path, string key)
        {
            var list = new List<KeyValuePair<string, Binding>>();
            if (Get(parent, key) is not { } node)
                return list;
            var p = Join(path, key);
            foreach (var (k, v) in Mapping(node, p).Children)
            {
                var name = Scalar(k, p);
                list.Add(new(name, ReadBinding(v, Join(p, name))));
            }
            return list;
        }

        private BodySpec ReadBody(YamlMappingNode m, string path)
        {
            OnlyKeys(m, path, "contentType", "template", "raw");
            var contentType = OptionalScalar(m, "contentType") ?? BodySpec.Json;
            if (!BodySpec.ContentTypes.Contains(contentType))
                throw Fail(Join(path, "contentType"), $"contentType must be one of {string.Join(", ", BodySpec.ContentTypes)}");
            var template = Get(m, "template");
            var raw = Get(m, "raw");
            if ((template == null) == (raw == null))
                throw Fail(path, "body needs exactly one of `template` or `raw`");
            if (template != null)
                return new BodySpec(contentType, ReadBodyNode(template, Join(path, "template")), null, Array.Empty<RawPlaceholder>());

            var rawText = Scalar(raw!, Join(path, "raw"));
            var placeholders = new List<RawPlaceholder>();
            foreach (Match match in RawPlaceholderRegex().Matches(rawText))
                placeholders.Add(new RawPlaceholder(match.Value, RawBinding(match, Join(path, "raw"))));
            return new BodySpec(contentType, null, rawText, placeholders);
        }

        private Binding RawBinding(Match match, string path)
        {
            var src = match.Groups[1].Value;
            var name = match.Groups[2].Success ? match.Groups[2].Value : null;
            var none = BindingModifiers.None;
            return (src, name) switch
            {
                ("id", _) => new IdBinding(name, none),
                ("col", not null) => new ColBinding(name, false, none),
                ("context", not null) => new ContextBinding(name, none),
                ("instance", not null) => new InstanceBinding(name, none),
                ("secret", not null) => new SecretBinding(name, none),
                ("computed", not null) => new ComputedBinding(name, none),
                _ => throw Fail(path, $"placeholder `{match.Value}` is not one of col.X, context.X, instance.X, computed.X, id"),
            };
        }

        private BodyNode ReadBodyNode(YamlNode node, string path)
        {
            switch (node)
            {
                case YamlSequenceNode seq:
                    return new BodyArray(seq.Children.Select((c, i) => ReadBodyNode(c, $"{path}[{i}]")).ToList());
                case YamlMappingNode map when map.Children.Keys.Any(k => k is YamlScalarNode s && IsBindingKey(s.Value!)):
                    return new BodyLeaf(ReadBinding(map, path));
                case YamlMappingNode map:
                    return new BodyObject(map.Children.Select(kv =>
                    {
                        var name = Scalar(kv.Key, path);
                        return new KeyValuePair<string, BodyNode>(name, ReadBodyNode(kv.Value, Join(path, name)));
                    }).ToList());
                default:
                    return new BodyLeaf(ReadBinding(node, path));
            }
        }

        private static bool IsBindingKey(string key) =>
            SourceKeys.Contains(key) || ModifierKeys.Contains(key) || SourceExtras.Values.Any(v => v.Contains(key));

        private Binding ReadBinding(YamlNode node, string path)
        {
            if (node is YamlScalarNode scalar)
                return new LiteralBinding(scalar.Value ?? "", scalar.Style != ScalarStyle.Plain);
            var m = Mapping(node, path);
            var keys = m.Children.Keys.Select(k => Scalar(k, path)).ToList();
            var sources = keys.Where(SourceKeys.Contains).ToList();
            if (sources.Count != 1)
                throw Fail(path, sources.Count == 0
                    ? $"a binding needs exactly one source key ({string.Join(", ", SourceKeys.Take(10))}); found none"
                    : $"a binding needs exactly one source key; found {string.Join(", ", sources)}");
            var src = sources[0];
            if (src == "step")
                throw Fail(path, "`step` bindings are not supported until a later phase");
            if (src == "id" && _customApi)
                throw Fail(path, "`id` is not available in a Custom API descriptor: only unbound Custom APIs are supported (HLD D-19)");

            var allowed = ModifierKeys.Append(src).Concat(SourceExtras.GetValueOrDefault(src, Array.Empty<string>())).ToArray();
            OnlyKeys(m, path, allowed);

            var format = OptionalScalar(m, "format");
            if (format != null && !Formats.All.Contains(format))
                throw Fail(Join(path, "format"), $"format must be one of {string.Join(", ", Formats.All)}; got `{format}`");
            var mods = new BindingModifiers(
                Get(m, "required") is { } r ? Bool(r, Join(path, "required")) : null,
                Get(m, "omitIfNull") is { } o ? Bool(o, Join(path, "omitIfNull")) : null,
                format);
            var value = m.Children[new YamlScalarNode(src)];
            var vp = Join(path, src);

            switch (src)
            {
                case "static":
                    var s = value as YamlScalarNode ?? throw Fail(vp, "static takes a scalar");
                    return new StaticBinding(s.Value ?? "", s.Style != ScalarStyle.Plain, mods);
                case "instance": return new InstanceBinding(Scalar(value, vp), mods);
                case "secret": return new SecretBinding(Scalar(value, vp), mods);
                case "context": return new ContextBinding(Scalar(value, vp), mods);
                case "computed": return new ComputedBinding(Scalar(value, vp), mods);
                case "col":
                    return new ColBinding(Scalar(value, vp), Get(m, "alwaysSend") is { } a && Bool(a, Join(path, "alwaysSend")), mods,
                        Get(m, "optionset") is { } os ? Scalar(os, Join(path, "optionset")) : null);
                case "param":
                    var param = Scalar(value, vp);
                    if (!ParamNameRegex().IsMatch(param))
                        throw Fail(vp, $"`{param}` is not a valid Custom API parameter name");
                    return new ParamBinding(param, mods);
                case "id":
                    var id = Scalar(value, vp);
                    return new IdBinding(id == "true" ? null : id, mods);
                case "filter":
                    var opsNode = Get(m, "ops") ?? throw Fail(Join(path, "ops"), "a filter binding must declare `ops`");
                    var ops = Sequence(opsNode, Join(path, "ops")).Select((n, i) => Scalar(n, $"{path}.ops[{i}]")).ToList();
                    foreach (var op in ops)
                        if (op == "null")
                            throw Fail(Join(path, "ops"), "the `null` operator is not supported in phase 1");
                        else if (!OpNames.Contains(op))
                            throw Fail(Join(path, "ops"), $"unknown operator `{op}`; use {string.Join(", ", OpNames)}");
                    if (ops.Count == 0)
                        throw Fail(Join(path, "ops"), "a filter binding must declare at least one operator");
                    return new FilterBinding(Scalar(value, vp), ops, mods);
                case "sort":
                    var fields = Mapping(value, vp).Children.ToDictionary(kv => Scalar(kv.Key, vp), kv => Scalar(kv.Value, vp));
                    return new SortBinding(fields, OptionalScalar(m, "descFormat"), mods);
                case "paging":
                    return new PagingBinding(Scalar(value, vp), Get(m, "max") is { } max ? Int(max, Join(path, "max")) : null, mods);
                default:
                    throw Fail(path, $"unhandled binding source `{src}`");
            }
        }

        private ResponseSpec ReadResponse(YamlMappingNode m, string path)
        {
            OnlyKeys(m, path, "rows", "key", "map", "unmapped", "outputs");
            var rows = OptionalScalar(m, "rows");
            if (rows != null)
                CheckJsonPath(rows, Join(path, "rows"));

            KeySpec? key = null;
            if (Get(m, "key") is { } keyNode)
            {
                var kp = Join(path, "key");
                if (keyNode is YamlScalarNode)
                {
                    key = new PathKey(CheckJsonPath(Scalar(keyNode, kp), kp));
                }
                else
                {
                    var km = Mapping(keyNode, kp);
                    if (Get(km, "header") != null)
                    {
                        OnlyKeys(km, kp, "header", "pattern");
                        var pattern = RequiredScalar(km, kp, "pattern");
                        Regex regex;
                        try { regex = new Regex(pattern); }
                        catch (ArgumentException ex) { throw Fail(Join(kp, "pattern"), $"invalid regular expression: {ex.Message}"); }
                        if (!regex.GetGroupNames().Contains("id"))
                            throw Fail(Join(kp, "pattern"), "the pattern must capture a named group `id`");
                        key = new HeaderKey(RequiredScalar(km, kp, "header"), pattern);
                    }
                    else
                    {
                        key = new CompositeKey(km.Children.ToDictionary(kv => Scalar(kv.Key, kp), kv => CheckJsonPath(Scalar(kv.Value, kp), Join(kp, Scalar(kv.Key, kp)))));
                    }
                }
            }

            var unmapped = Get(m, "unmapped") is { } u
                ? Sequence(u, Join(path, "unmapped")).Select((n, i) => Scalar(n, $"{path}.unmapped[{i}]")).ToList()
                : new List<string>();
            return new ResponseSpec(rows, key, ReadEntries(m, path, "map", outputs: false), unmapped, ReadEntries(m, path, "outputs", outputs: true));
        }

        /// <summary><c>response.map</c> (column to entry) or <c>response.outputs</c> (output parameter to entry).</summary>
        private Dictionary<string, MapEntry> ReadEntries(YamlMappingNode m, string path, string key, bool outputs)
        {
            var entries = new Dictionary<string, MapEntry>();
            if (Get(m, key) is not { } node)
                return entries;
            var mp = Join(path, key);
            foreach (var (k, v) in Mapping(node, mp).Children)
            {
                var name = Scalar(k, mp);
                var ep = Join(mp, name);
                if (outputs && !ParamNameRegex().IsMatch(name))
                    throw Fail(ep, $"`{name}` is not a valid Custom API parameter name");
                if (v is YamlScalarNode)
                {
                    entries[name] = new MapEntry(CheckJsonPath(Scalar(v, ep), ep), null, false);
                    continue;
                }
                var em = Mapping(v, ep);
                if (Get(em, "jsonata") != null)
                    throw Fail(ep, "`jsonata` transforms are not supported until a later phase");
                OnlyKeys(em, ep, "path", "type", "optional");
                var type = OptionalScalar(em, "type");
                if (type == "money")
                    throw Fail(Join(ep, "type"), "type money is not available: virtual tables cannot have Money columns (HLD 15); map amounts with type decimal");
                if (outputs && type != null && !MapEntry.OutputTypes.Contains(type))
                    throw Fail(Join(ep, "type"), $"an output's type must be one of {string.Join(", ", MapEntry.OutputTypes)}; got `{type}`");
                if (type != null && !MapEntry.Types.Contains(type) && !type.StartsWith("optionset:", StringComparison.Ordinal))
                    throw Fail(Join(ep, "type"), $"type must be one of {string.Join(", ", MapEntry.Types)} or optionset:<map>; got `{type}`");
                entries[name] = new MapEntry(
                    CheckJsonPath(RequiredScalar(em, ep, "path"), Join(ep, "path")),
                    type,
                    Get(em, "optional") is { } opt && Bool(opt, Join(ep, "optional")));
            }
            return entries;
        }

        private PagingSpec ReadPaging(YamlMappingNode m, string path)
        {
            OnlyKeys(m, path, "strategy", "pageSize", "token", "offset", "page", "maxCallsPerPage");
            var strategy = RequiredScalar(m, path, "strategy");
            if (!PagingSpec.Strategies.Contains(strategy))
                throw Fail(Join(path, "strategy"), $"strategy must be one of {string.Join(", ", PagingSpec.Strategies)}");

            PageSizeSpec? pageSize = null;
            if (Get(m, "pageSize") is { } ps)
            {
                var p = Join(path, "pageSize");
                var pm = Mapping(ps, p);
                OnlyKeys(pm, p, "param", "max");
                var max = Int(Require(pm, p, "max"), Join(p, "max"));
                if (max < 1)
                    throw Fail(Join(p, "max"), "max must be 1 or more");
                pageSize = new PageSizeSpec(RequiredScalar(pm, p, "param"), max);
            }
            TokenSpec? token = null;
            if (Get(m, "token") is { } t)
            {
                var p = Join(path, "token");
                var tm = Mapping(t, p);
                OnlyKeys(tm, p, "param", "next");
                token = new TokenSpec(RequiredScalar(tm, p, "param"), CheckJsonPath(RequiredScalar(tm, p, "next"), Join(p, "next")));
            }
            OffsetSpec? offset = null;
            if (Get(m, "offset") is { } o)
            {
                var p = Join(path, "offset");
                var om = Mapping(o, p);
                OnlyKeys(om, p, "param", "total");
                var total = OptionalScalar(om, "total");
                if (total != null)
                    CheckJsonPath(total, Join(p, "total"));
                offset = new OffsetSpec(RequiredScalar(om, p, "param"), total);
            }
            PageNumberSpec? page = null;
            if (Get(m, "page") is { } pg)
            {
                var p = Join(path, "page");
                var pm = Mapping(pg, p);
                OnlyKeys(pm, p, "param", "base");
                var @base = Get(pm, "base") is { } b ? Int(b, Join(p, "base")) : 1;
                if (@base is not (0 or 1))
                    throw Fail(Join(p, "base"), "base must be 0 or 1");
                page = new PageNumberSpec(RequiredScalar(pm, p, "param"), @base);
            }
            var maxCalls = Get(m, "maxCallsPerPage") is { } mc ? Int(mc, Join(path, "maxCallsPerPage")) : PagingSpec.DefaultMaxCallsPerPage;
            if (maxCalls < 1)
                throw Fail(Join(path, "maxCallsPerPage"), "maxCallsPerPage must be 1 or more");
            return new PagingSpec(strategy, pageSize, token, offset, page, maxCalls);
        }

        private ErrorsSpec ReadErrors(YamlMappingNode m, string path)
        {
            var statuses = new Dictionary<int, ErrorMapping>();
            string? @default = null;
            SuccessBodyCheck? check = null;
            foreach (var (k, v) in m.Children)
            {
                var key = Scalar(k, path);
                var p = Join(path, key);
                if (key == "default")
                {
                    @default = Scalar(v, p);
                }
                else if (key == "successBodyCheck")
                {
                    var cm = Mapping(v, p);
                    OnlyKeys(cm, p, "path", "equals");
                    check = new SuccessBodyCheck(CheckJsonPath(RequiredScalar(cm, p, "path"), Join(p, "path")), RequiredScalar(cm, p, "equals"));
                }
                else if (int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var status) && status is >= 100 and <= 599)
                {
                    ErrorMapping mapping;
                    if (v is YamlScalarNode)
                    {
                        mapping = new ErrorMapping(Scalar(v, p), null);
                    }
                    else
                    {
                        var em = Mapping(v, p);
                        OnlyKeys(em, p, "exception", "message");
                        var msg = OptionalScalar(em, "message");
                        if (msg != null)
                            CheckJsonPath(msg, Join(p, "message"));
                        mapping = new ErrorMapping(RequiredScalar(em, p, "exception"), msg);
                    }
                    if (!ErrorMapping.Kinds.Contains(mapping.Exception))
                        throw Fail(p, $"exception must be one of {string.Join(", ", ErrorMapping.Kinds)}; got `{mapping.Exception}`");
                    statuses[status] = mapping;
                }
                else
                {
                    throw Fail(p, $"unknown key `{key}`; errors takes HTTP status codes, `default` and `successBodyCheck`");
                }
            }
            return new ErrorsSpec(statuses, @default, check);
        }

        private RetrySpec ReadRetry(YamlMappingNode m, string path)
        {
            OnlyKeys(m, path, "idempotent", "attempts", "on");
            var attempts = Get(m, "attempts") is { } a ? Int(a, Join(path, "attempts")) : 3;
            if (attempts is < 1 or > 10)
                throw Fail(Join(path, "attempts"), "attempts must be from 1 to 10");
            var on = Get(m, "on") is { } onNode
                ? Sequence(onNode, Join(path, "on")).Select((n, i) => Scalar(n, $"{path}.on[{i}]")).ToList()
                : new List<string> { "429", "503", "network" };
            foreach (var value in on)
                if (value != "network" && !(int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var s) && s is >= 400 and <= 599))
                    throw Fail(Join(path, "on"), $"retry.on takes 4xx/5xx status codes or `network`; got `{value}`");
            return new RetrySpec(Get(m, "idempotent") is { } i && Bool(i, Join(path, "idempotent")), attempts, on);
        }

        private Dictionary<string, OptionSetMap> ReadOptionSets(YamlMappingNode m, string path)
        {
            var result = new Dictionary<string, OptionSetMap>();
            foreach (var (k, v) in m.Children)
            {
                var name = Scalar(k, path);
                var p = Join(path, name);
                var values = new Dictionary<string, int>();
                var unknownNull = false;
                foreach (var (vk, vv) in Mapping(v, p).Children)
                {
                    var sourceValue = Scalar(vk, p);
                    if (sourceValue == "_unknown")
                    {
                        var mode = Scalar(vv, Join(p, "_unknown"));
                        unknownNull = mode switch
                        {
                            "null" => true,
                            "error" => false,
                            _ => throw Fail(Join(p, "_unknown"), "_unknown must be `error` or `null`"),
                        };
                        continue;
                    }
                    values[sourceValue] = Int(vv, Join(p, sourceValue));
                }
                if (values.Values.Distinct().Count() != values.Count)
                    throw Fail(p, "two source values map to the same option value, so filters could not be reverse-mapped");
                result[name] = new OptionSetMap(values, unknownNull);
            }
            return result;
        }

        private FiltersSpec ReadFilters(YamlMappingNode m, string path)
        {
            OnlyKeys(m, path, "allowOr", "orStyle");
            var orStyle = OptionalScalar(m, "orStyle");
            if (orStyle != null && orStyle != "in")
                throw Fail(Join(path, "orStyle"), "orStyle supports only `in` in v1");
            return new FiltersSpec(Get(m, "allowOr") is { } a && Bool(a, Join(path, "allowOr")), orStyle);
        }

        private string CheckJsonPath(string path, string yamlPath)
        {
            if (!JsonPath.TryParse(path, out _))
                throw Fail(yamlPath, $"`{path}` is not a valid JSONPath (RFC 9535)");
            return path;
        }

        // ---- YAML node helpers ----

        private YamlMappingNode Mapping(YamlNode node, string path) =>
            node as YamlMappingNode ?? throw Fail(path, "expected a mapping");

        private YamlSequenceNode Sequence(YamlNode node, string path) =>
            node as YamlSequenceNode ?? throw Fail(path, "expected a list");

        private string Scalar(YamlNode node, string path) =>
            (node as YamlScalarNode)?.Value ?? throw Fail(path, "expected a scalar");

        private static YamlNode? Get(YamlMappingNode m, string key) =>
            m.Children.TryGetValue(new YamlScalarNode(key), out var v) ? v : null;

        private YamlNode Require(YamlMappingNode m, string path, string key) =>
            Get(m, key) ?? throw Fail(Join(path, key), $"`{key}` is required");

        private string RequiredScalar(YamlMappingNode m, string path, string key) => Scalar(Require(m, path, key), Join(path, key));

        private string? OptionalScalar(YamlMappingNode m, string key) =>
            Get(m, key) is { } node ? Scalar(node, key) : null;

        private int Int(YamlNode node, string path) =>
            int.TryParse(Scalar(node, path), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v)
                ? v
                : throw Fail(path, "expected an integer");

        private bool Bool(YamlNode node, string path) => Scalar(node, path) switch
        {
            "true" => true,
            "false" => false,
            var other => throw Fail(path, $"expected true or false; got `{other}`"),
        };

        private void OnlyKeys(YamlMappingNode m, string path, params string[] allowed)
        {
            foreach (var k in m.Children.Keys)
            {
                var key = Scalar(k, path);
                if (!allowed.Contains(key))
                    throw Fail(Join(path, key), $"unknown key `{key}`; allowed here: {string.Join(", ", allowed)}");
            }
        }
    }
}
