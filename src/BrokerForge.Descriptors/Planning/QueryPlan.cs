namespace BrokerForge.Descriptors.Planning;

/// <summary>
/// A canonical query checked against the descriptor's declarations. <see cref="ByParam"/> is keyed by
/// the query or path parameter name of the filter binding that receives the condition.
/// <see cref="PageSize"/> is the size of one source call (capped at paging.pageSize.max);
/// <see cref="RequestedSize"/> is the Dataverse page the paging coordinator fills.
/// </summary>
public sealed record QueryPlan(IReadOnlyDictionary<string, BoundFilter> ByParam, BoundSort? Sort, int PageSize, string? Token, int RequestedSize);

/// <summary>A condition bound to one parameter. Values are already in the source's form (option sets reverse-mapped).</summary>
public sealed record BoundFilter(string Op, IReadOnlyList<string> SourceValues);

public sealed record BoundSort(string SourceField, bool Desc);
