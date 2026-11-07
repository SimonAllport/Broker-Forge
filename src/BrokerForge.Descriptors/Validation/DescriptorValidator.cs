using System.Text.RegularExpressions;
using BrokerForge.Contracts;
using BrokerForge.Descriptors.Model;
using BrokerForge.Schema;

namespace BrokerForge.Descriptors.Validation;

/// <summary>A validation failure. <see cref="Rule"/> is the descriptor spec 12 rule number.</summary>
public sealed record DescriptorError(int Rule, string YamlPath, string Message)
{
    public override string ToString() => $"rule {Rule}: {YamlPath}: {Message}";
}

/// <summary>
/// Whole-descriptor rules (descriptor spec 12). Rule 1 is enforced by the parser; rules 10 and 11
/// cover features the parser still rejects; rule 7 runs only when the table schema (spec 14) is given.
/// </summary>
public static partial class DescriptorValidator
{
    private enum Location { Path, Query, Header, Body }

    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex PathPlaceholderRegex();

    public static IReadOnlyList<DescriptorError> Validate(OperationDescriptor d, ReservedNames reserved, SourceSchema? schema = null)
    {
        var errors = new List<DescriptorError>();
        var r = d.Request;

        foreach (var (name, binding) in r.PathParams)
            CheckBinding(d, binding, Location.Path, $"request.pathParams.{name}", inTemplate: false, errors);
        foreach (var (name, binding) in r.Query)
        {
            CheckBinding(d, binding, Location.Query, $"request.query.{name}", inTemplate: false, errors);
            if (reserved.ContainsQueryParam(name))
                errors.Add(new(4, $"request.query.{name}", $"`{name}` is reserved: the service instance's authentication mode sends it"));
        }
        foreach (var (where, param) in new[] { ("paging.pageSize.param", d.Paging?.PageSize?.Param), ("paging.token.param", d.Paging?.Token?.Param),
                     ("paging.offset.param", d.Paging?.Offset?.Param), ("paging.page.param", d.Paging?.Page?.Param) })
            if (param != null && reserved.ContainsQueryParam(param))
                errors.Add(new(4, where, $"`{param}` is reserved: the service instance's authentication mode sends it"));
        foreach (var (name, binding) in r.Headers)
        {
            CheckBinding(d, binding, Location.Header, $"request.headers.{name}", inTemplate: false, errors);
            if (reserved.Contains(name))
                errors.Add(new(4, $"request.headers.{name}", $"`{name}` is reserved: it is set by the service instance's authentication mode"));
        }
        if (r.Body is { } body)
        {
            if (body.Template != null)
                WalkTemplate(d, body.Template, "request.body.template", errors);
            foreach (var placeholder in body.RawPlaceholders)
                CheckBinding(d, placeholder.Binding, Location.Body, "request.body.raw", inTemplate: false, errors);
            if (r.Method is "GET" or "DELETE")
                errors.Add(new(5, "request.body", $"{r.Method} must not have a body"));
        }

        CheckPathParams(r, errors);
        CheckReadsAndPaging(d, errors);

        if (d.Errors?.Default is { } def && def != "Fail")
            errors.Add(new(8, "errors.default", $"errors.default must be Fail; got `{def}`"));

        if (d.Retry is { } retry)
        {
            if (!retry.Idempotent)
                errors.Add(new(9, "retry", "retry needs `idempotent: true`"));
            // A POST is not safe to repeat, whatever it binds: the source tells a repeat from a new call only by the key.
            if ((d.Binds == Messages.Create || string.Equals(r.Method, "POST", StringComparison.OrdinalIgnoreCase)) && !r.Headers.Any(h =>
                    string.Equals(h.Key, "Idempotency-Key", StringComparison.OrdinalIgnoreCase) && h.Value is ComputedBinding { Kind: "uuid" }))
                errors.Add(new(9, "retry", "Create or a POST can only retry with an `Idempotency-Key: { computed: uuid }` header"));
        }
        if (schema != null)
            CheckSchema(d, schema, errors);
        return errors;
    }

    /// <summary>Every binding in the request with its YAML path, in file order.</summary>
    private static IEnumerable<(string Path, Binding Binding)> Bindings(RequestSpec r)
    {
        foreach (var (name, b) in r.PathParams) yield return ($"request.pathParams.{name}", b);
        foreach (var (name, b) in r.Query) yield return ($"request.query.{name}", b);
        foreach (var (name, b) in r.Headers) yield return ($"request.headers.{name}", b);
        if (r.Body?.Template is { } template)
            foreach (var item in Leaves(template, "request.body.template")) yield return item;
        foreach (var placeholder in r.Body?.RawPlaceholders ?? Array.Empty<RawPlaceholder>())
            yield return ("request.body.raw", placeholder.Binding);
    }

    private static IEnumerable<(string, Binding)> Leaves(BodyNode node, string path) => node switch
    {
        BodyObject obj => obj.Members.SelectMany(m => Leaves(m.Value, $"{path}.{m.Key}")),
        BodyArray arr => arr.Items.SelectMany((item, i) => Leaves(item, $"{path}[{i}]")),
        BodyLeaf leaf => new[] { (path, leaf.Binding) },
        _ => Array.Empty<(string, Binding)>(),
    };

    /// <summary>Rule 7: the descriptor and the table schema agree (descriptor spec 12 and 14).</summary>
    private static void CheckSchema(OperationDescriptor d, SourceSchema schema, List<DescriptorError> errors)
    {
        void Rule7(string path, string message) => errors.Add(new(7, path, message));
        if (d.CustomApi is { } api)
        {
            CheckCustomApiSchema(d, api, schema, Rule7);
            return;
        }
        if (d.Table == null)
            return;
        if (d.Table != schema.Table)
        {
            Rule7("table", $"table `{d.Table}` is not the schema's table `{schema.Table ?? "(none)"}`");
            return;
        }
        bool Known(string col) => schema.Columns.ContainsKey(col) || col == schema.PrimaryKey;
        string Unknown(string col) => $"column `{col}` is not in the table schema for {schema.Table}";

        var map = d.Response?.Map ?? new Dictionary<string, MapEntry>();
        var unmapped = d.Response?.Unmapped ?? Array.Empty<string>();
        foreach (var col in map.Keys.Where(c => !Known(c)))
            Rule7($"response.map.{col}", Unknown(col));
        for (var i = 0; i < unmapped.Count; i++)
            if (!Known(unmapped[i]))
                Rule7($"response.unmapped[{i}]", Unknown(unmapped[i]));

        var bound = new HashSet<string>();
        foreach (var (path, binding) in Bindings(d.Request))
        {
            var cols = binding switch
            {
                ColBinding c => new[] { c.Column },
                FilterBinding f => new[] { f.Column },
                SortBinding s => s.Fields.Keys.ToArray(),
                _ => Array.Empty<string>(),
            };
            foreach (var col in cols)
                if (!Known(col))
                    Rule7(path, Unknown(col));
            if (binding is ColBinding cb)
            {
                bound.Add(cb.Column);
                if (schema.Columns.TryGetValue(cb.Column, out var column))
                {
                    if (cb.OptionSet is { } setName && column.Type != "optionset")
                        Rule7(path, $"`{cb.Column}` is a {column.Type} column in the schema, not an option set");
                    else if (cb.OptionSet is { } name && d.Optionsets.TryGetValue(name, out var set))
                        CompareOptions(set, column.Options!, $"optionsets.{name}", cb.Column, Rule7);
                }
            }
        }

        // Reads must account for every column; so must Create, whose body either sends a column or lists it as unmapped.
        var covered = d.IsRead ? map.Keys.ToHashSet() : d.Binds == Messages.Create ? bound : null;
        if (covered != null)
        {
            var missing = schema.Columns.Keys.Where(c => !covered.Contains(c) && !unmapped.Contains(c)).ToList();
            if (missing.Count > 0)
                Rule7(d.IsRead ? "response.map" : "request.body",
                    $"{string.Join(", ", missing.Select(c => $"`{c}`"))} {(missing.Count == 1 ? "is" : "are")} neither {(d.IsRead ? "mapped" : "sent")} nor listed in response.unmapped");
        }

        foreach (var (col, entry) in map)
        {
            if (!schema.Columns.TryGetValue(col, out var column))
                continue;
            if (entry.OptionSetName is { } setName)
            {
                if (column.Type != "optionset")
                    Rule7($"response.map.{col}", $"`{col}` is a {column.Type} column in the schema, not an option set");
                else if (d.Optionsets.TryGetValue(setName, out var set))
                    CompareOptions(set, column.Options!, $"optionsets.{setName}", col, Rule7);
            }
            else if (column.Type == "optionset")
            {
                Rule7($"response.map.{col}", $"`{col}` is an option set column; map it with type optionset:<map>");
            }
        }
    }

    private static void CheckCustomApiSchema(OperationDescriptor d, string api, SourceSchema schema, Action<string, string> rule7)
    {
        if (!schema.CustomApis.TryGetValue(api, out var declared))
        {
            rule7("binds", $"Custom API `{api}` is not declared under customApis in the schema");
            return;
        }
        var sent = new HashSet<string>();
        foreach (var (path, binding) in Bindings(d.Request))
            if (binding is ParamBinding p)
            {
                sent.Add(p.Name);
                if (!declared.Inputs.ContainsKey(p.Name))
                    rule7(path, $"parameter `{p.Name}` is not an input of {api} in the schema");
            }
        // An input the request never sends would be accepted and dropped while the call reports success.
        var unsent = declared.Inputs.Keys.Where(i => !sent.Contains(i)).ToList();
        if (unsent.Count > 0)
            rule7("request", $"input(s) {string.Join(", ", unsent.Select(i => $"`{i}`"))} of {api} are declared in the schema but no `param` binding sends them");
        var outputs = d.Response?.Outputs ?? new Dictionary<string, MapEntry>();
        foreach (var (name, entry) in outputs)
            if (!declared.Outputs.TryGetValue(name, out var type))
                rule7($"response.outputs.{name}", $"`{name}` is not an output of {api} in the schema");
            else if ((entry.Type ?? "string") != type)
                rule7($"response.outputs.{name}", $"`{name}` is {entry.Type ?? "string"} here but {type} in the schema");
        var missing = declared.Outputs.Keys.Where(o => !outputs.ContainsKey(o)).ToList();
        if (missing.Count > 0)
            rule7("response.outputs", $"{string.Join(", ", missing.Select(o => $"`{o}`"))} declared in the schema but not mapped");
    }

    private static void CompareOptions(OptionSetMap set, IReadOnlyDictionary<string, int> options, string path, string col, Action<string, string> rule7)
    {
        var problems = new List<string>();
        foreach (var (label, value) in options)
            if (!set.Values.TryGetValue(label, out var mapped))
                problems.Add($"`{label}` ({value}) is missing");
            else if (mapped != value)
                problems.Add($"`{label}` is {mapped} here but {value} in the schema");
        foreach (var label in set.Values.Keys.Where(l => !options.ContainsKey(l)))
            problems.Add($"`{label}` is not a schema option");
        if (problems.Count > 0)
            rule7(path, $"does not match the options of `{col}`: {string.Join("; ", problems)}");
    }

    private static void WalkTemplate(OperationDescriptor d, BodyNode node, string path, List<DescriptorError> errors)
    {
        switch (node)
        {
            case BodyObject obj:
                foreach (var (name, child) in obj.Members)
                    WalkTemplate(d, child, $"{path}.{name}", errors);
                break;
            case BodyArray arr:
                for (var i = 0; i < arr.Items.Count; i++)
                    WalkTemplate(d, arr.Items[i], $"{path}[{i}]", errors);
                break;
            case BodyLeaf leaf:
                CheckBinding(d, leaf.Binding, Location.Body, path, inTemplate: true, errors);
                break;
        }
    }

    private static void CheckBinding(OperationDescriptor d, Binding binding, Location location, string path, bool inTemplate, List<DescriptorError> errors)
    {
        void Rule2(string message) => errors.Add(new(2, path, message));
        var rm = d.Binds == Messages.RetrieveMultiple;

        switch (binding)
        {
            case SecretBinding when location is Location.Path or Location.Body:
                errors.Add(new(12, path, "`secret` is not allowed in the path or body, where a source could echo it back"));
                break;
            case ContextBinding c when !ContextBinding.Names.Contains(c.Name):
                Rule2($"unknown context `{c.Name}`; use {string.Join(", ", ContextBinding.Names)}");
                break;
            case ComputedBinding c when !ComputedBinding.Kinds.Contains(c.Kind):
                Rule2($"unknown computed value `{c.Kind}`; use {string.Join(", ", ComputedBinding.Kinds)}");
                break;
            case ColBinding when d.Binds is not (Messages.Create or Messages.Update):
                Rule2($"`col` is only allowed on Create and Update, not {d.Binds}");
                break;
            case IdBinding when d.Binds is not (Messages.Retrieve or Messages.Update or Messages.Delete):
                Rule2($"`id` is only allowed on Retrieve, Update and Delete, not {d.Binds}");
                break;
            case FilterBinding when !rm || location is not (Location.Query or Location.Path):
                Rule2("`filter` is only allowed in the query or path of a RetrieveMultiple");
                break;
            case SortBinding when !rm || location != Location.Query:
                Rule2("`sort` is only allowed in the query of a RetrieveMultiple");
                break;
            case SortBinding { DescFormat: { } fmt } when !fmt.Contains("{field}"):
                Rule2("descFormat must contain `{field}`");
                break;
            case PagingBinding p when !rm || location != Location.Query || !PagingBinding.Kinds.Contains(p.Kind):
                Rule2($"`paging` must be one of {string.Join(", ", PagingBinding.Kinds)}, in the query of a RetrieveMultiple");
                break;
            case ParamBinding when d.CustomApi == null:
                Rule2($"`param` is only allowed in a Custom API descriptor, not {d.Binds}");
                break;
        }
        if (binding is ColBinding { OptionSet: { } set } && !d.Optionsets.ContainsKey(set))
            errors.Add(new(6, path, $"option set map `{set}` is not declared under optionsets"));

        if (binding.Modifiers.Format == "json" && !inTemplate)
            Rule2("`format: json` is only allowed on body template leaves");
        if (binding is FilterBinding f)
        {
            if (d.Response?.Map.ContainsKey(f.Column) == false && d.Response.Unmapped.Contains(f.Column) == false)
                Rule2($"filtered column `{f.Column}` is not in response.map");
            if (f.Ops.Contains(Ops.In) && binding.Modifiers.Format != "csv")
                Rule2("a filter that accepts `in` needs `format: csv` so the values have a defined form");
        }
    }

    private static void CheckPathParams(RequestSpec r, List<DescriptorError> errors)
    {
        var used = PathPlaceholderRegex().Matches(r.Path).Select(m => m.Groups[1].Value).ToHashSet();
        foreach (var name in used.Where(n => !r.PathParams.ContainsKey(n)))
            errors.Add(new(3, "request.path", $"placeholder `{{{name}}}` has no pathParams entry"));
        foreach (var name in r.PathParams.Keys.Where(n => !used.Contains(n)))
            errors.Add(new(3, $"request.pathParams.{name}", $"pathParams entry `{name}` is not used in the path"));
    }

    private static void CheckReadsAndPaging(OperationDescriptor d, List<DescriptorError> errors)
    {
        var resp = d.Response;
        if (d.IsRead && resp?.Rows == null)
            errors.Add(new(6, "response.rows", $"{d.Binds} needs response.rows"));
        if (d.Binds is Messages.Retrieve or Messages.RetrieveMultiple or Messages.Create && resp?.Key == null)
            errors.Add(new(6, "response.key", $"{d.Binds} needs response.key"));
        if (resp?.Key is HeaderKey && d.Binds != Messages.Create)
            errors.Add(new(6, "response.key", "a header key is only meaningful on Create"));

        if (resp != null)
            foreach (var (col, entry) in resp.Map)
                if (entry.OptionSetName is { } set && !d.Optionsets.ContainsKey(set))
                    errors.Add(new(6, $"response.map.{col}", $"option set map `{set}` is not declared under optionsets"));

        if (d.CustomApi != null && resp != null)
        {
            foreach (var (key, present) in new[] { ("rows", resp.Rows != null), ("key", resp.Key != null), ("map", resp.Map.Count > 0), ("unmapped", resp.Unmapped.Count > 0) })
                if (present)
                    errors.Add(new(6, $"response.{key}", $"a Custom API returns outputs, not response.{key}"));
        }
        else if (d.CustomApi == null && resp?.Outputs.Count > 0)
        {
            errors.Add(new(6, "response.outputs", $"response.outputs is only for Custom APIs, not {d.Binds}"));
        }

        if (d.Binds != Messages.RetrieveMultiple)
        {
            if (d.Paging != null)
                errors.Add(new(6, "paging", "paging is only allowed on RetrieveMultiple"));
            return;
        }
        if (d.SortStable == null)
            errors.Add(new(6, "sortStable", "RetrieveMultiple must declare sortStable: true or false so paging behaviour is known"));
        if (d.Paging is not { } paging)
        {
            errors.Add(new(6, "paging", "RetrieveMultiple needs paging"));
            return;
        }
        switch (paging.Strategy)
        {
            case "cursor" when paging.Token == null:
                errors.Add(new(6, "paging.token", "cursor paging needs token: { param, next }"));
                break;
            case "offset" when paging.Offset == null:
                errors.Add(new(6, "paging.offset", "offset paging needs offset: { param }"));
                break;
            case "pageNumber" when paging.Page == null:
                errors.Add(new(6, "paging.page", "pageNumber paging needs page: { param, base }"));
                break;
        }
        if (paging.Strategy is "offset" or "pageNumber" && paging.PageSize == null)
            errors.Add(new(6, "paging.pageSize", $"{paging.Strategy} paging needs pageSize to know where the next page starts"));
    }
}
