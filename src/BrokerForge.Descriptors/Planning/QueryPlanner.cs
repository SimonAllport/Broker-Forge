using BrokerForge.Contracts;
using BrokerForge.Descriptors.Model;

namespace BrokerForge.Descriptors.Planning;

/// <summary>
/// Checks a canonical query against what the descriptor declares (descriptor spec 5) and binds it.
/// Anything undeclared is an <see cref="UnsupportedQueryException"/> naming the column and operator:
/// the host never filters, sorts or trims client-side.
/// </summary>
public static class QueryPlanner
{
    private const int FallbackPageSize = 50;

    public static QueryPlan Plan(OperationDescriptor d, CanonicalQuery q, string correlationId)
    {
        UnsupportedQueryException Reject(string message) => new($"{message} (operation {d.Operation}).", correlationId);

        var conditions = Flatten(d, q.Filter, Reject);
        var filterBindings = d.Request.Query.Concat(d.Request.PathParams)
            .Where(kv => kv.Value is FilterBinding)
            .Select(kv => (Param: kv.Key, Binding: (FilterBinding)kv.Value))
            .ToList();

        var byParam = new Dictionary<string, BoundFilter>();
        foreach (var c in conditions)
        {
            var values = c.Values ?? new List<string>();
            var candidates = filterBindings.Where(b => b.Binding.Column == c.Col).ToList();
            if (candidates.Count == 0)
                throw Reject($"Filter on column {c.Col} with operator {c.Op} is not supported: the descriptor declares no filter for {c.Col}");
            var match = candidates.FirstOrDefault(b => b.Binding.Ops.Contains(c.Op));
            if (match.Binding == null)
                throw Reject($"Filter on column {c.Col} with operator {c.Op} is not supported: {c.Col} accepts {string.Join(", ", candidates.SelectMany(b => b.Binding.Ops).Distinct())}");
            if (byParam.ContainsKey(match.Param))
                throw Reject($"Two filter conditions on column {c.Col} bind to the same parameter `{match.Param}`; the source accepts one");

            if (c.Op == Ops.In ? values.Count == 0 : values.Count != 1)
                throw Reject($"Filter on column {c.Col} with operator {c.Op} needs {(c.Op == Ops.In ? "at least one value" : "exactly one value")}");
            if (c.Op == Ops.Like && values.Any(v => v.Contains('%') || v.Contains('_')))
                throw Reject($"Filter on column {c.Col} with operator like supports begins-with only; the value may not contain % or _");

            var sourceValues = ReverseMap(d, c.Col, c.Op, values, Reject);
            // A csv parameter cannot carry a value that contains its own delimiter.
            if (match.Binding.Modifiers.Format == "csv" && sourceValues.Any(v => v.Contains(',')))
                throw Reject($"Filter on column {c.Col} with operator {c.Op}: a value contains a comma, which the source's comma-separated parameter cannot carry");
            byParam[match.Param] = new BoundFilter(c.Op, sourceValues);
        }

        var requested = q.Page?.Size > 0 ? q.Page.Size : d.Paging?.PageSize?.Max ?? FallbackPageSize;
        var perCall = d.Paging?.PageSize?.Max is { } max ? Math.Min(requested, max) : requested;
        return new QueryPlan(byParam, PlanSort(d, q.OrderBy, Reject), perCall, q.Page?.Token, requested);
    }

    /// <summary>
    /// Reduces the filter tree to a flat AND of conditions. An OR group is accepted only when
    /// filters.allowOr is true and it is eq on one column, which collapses to a single `in`.
    /// </summary>
    private static List<Condition> Flatten(OperationDescriptor d, FilterGroup? group, Func<string, Exception> reject)
    {
        var result = new List<Condition>();
        if (group == null)
            return result;
        if (string.Equals(group.Type, "or", StringComparison.OrdinalIgnoreCase))
        {
            result.Add(CollapseOr(d, group, reject));
            return result;
        }
        if (!string.Equals(group.Type, "and", StringComparison.OrdinalIgnoreCase))
            throw reject($"Filter group type `{group.Type}` is not and/or");
        result.AddRange(group.Conditions ?? new List<Condition>());
        foreach (var child in group.Groups ?? new List<FilterGroup>())
            result.AddRange(Flatten(d, child, reject));
        return result;
    }

    private static Condition CollapseOr(OperationDescriptor d, FilterGroup group, Func<string, Exception> reject)
    {
        var conditions = group.Conditions ?? new List<Condition>();
        var described = string.Join(" or ", conditions.Select(c => $"{c.Col} {c.Op}"));
        if (!d.Filters.AllowOr)
            throw reject($"An or group ({described}) is not supported: the descriptor does not set filters.allowOr");
        if (group.Groups is { Count: > 0 } || conditions.Count == 0)
            throw reject($"Nested or groups ({described}) are not supported");
        var columns = conditions.Select(c => c.Col).Distinct().ToList();
        if (columns.Count != 1 || conditions.Any(c => c.Op != Ops.Eq))
            throw reject($"An or group ({described}) is supported only as eq on one column, which becomes in");
        return new Condition { Col = columns[0], Op = Ops.In, Values = conditions.SelectMany(c => c.Values ?? new List<string>()).Distinct().ToList() };
    }

    /// <summary>Option set columns arrive as Dataverse integers; the source wants its own values (G4).</summary>
    private static List<string> ReverseMap(OperationDescriptor d, string col, string op, List<string> values, Func<string, Exception> reject)
    {
        if (d.Response?.Map.GetValueOrDefault(col)?.OptionSetName is not { } setName || !d.Optionsets.TryGetValue(setName, out var set))
            return values;
        var reverse = set.Values.ToDictionary(kv => kv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), kv => kv.Key);
        return values.Select(v => reverse.TryGetValue(v, out var source)
            ? source
            : throw reject($"Filter on column {col} with operator {op}: option value {v} has no source value in option set {setName}")).ToList();
    }

    private static BoundSort? PlanSort(OperationDescriptor d, List<OrderBy>? orderBy, Func<string, Exception> reject)
    {
        if (orderBy == null || orderBy.Count == 0)
            return null;
        if (orderBy.Count > 1)
            throw reject($"Sorting on more than one column ({string.Join(", ", orderBy.Select(o => o.Col))}) is not supported");
        var o = orderBy[0];
        var binding = d.Request.Query.Select(kv => kv.Value).OfType<SortBinding>().FirstOrDefault();
        if (binding == null || !binding.Fields.TryGetValue(o.Col, out var field))
            throw reject($"Sort on column {o.Col} is not supported: the descriptor declares no sort for {o.Col}");
        if (o.Desc && binding.DescFormat == null)
            throw reject($"Sort on column {o.Col} descending is not supported: the descriptor declares no descFormat");
        return new BoundSort(field, o.Desc);
    }
}
