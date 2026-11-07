using BrokerForge.Contracts;
using BrokerForge.Keys;

namespace BrokerForge.Descriptors.Mapping;

/// <summary>One source row: its external key and its cells. Cell values are invariant strings (datetimes ISO 8601 UTC "o", option sets as the integer).</summary>
public sealed record MappedRow(ExternalKey Key, IReadOnlyList<Cell> Cells);
