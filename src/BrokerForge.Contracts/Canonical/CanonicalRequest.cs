using System.Collections.Generic;
using System.Runtime.Serialization;

namespace BrokerForge.Contracts
{
    /// <summary>
    /// One shape for every message (HLD 7.1). Every value is an invariant string or a list of them,
    /// so DataContractJsonSerializer (plug-in) and System.Text.Json (host) read the same JSON.
    /// </summary>
    [DataContract]
    public sealed class CanonicalRequest
    {
        [DataMember(Name = "correlationId", EmitDefaultValue = false)] public string CorrelationId { get; set; }
        [DataMember(Name = "environmentId", EmitDefaultValue = false)] public string EnvironmentId { get; set; }
        /// <summary>Null for an unbound Custom API: the host finds the instance from the descriptor that binds it (D-18).</summary>
        [DataMember(Name = "serviceInstance", EmitDefaultValue = false)] public string ServiceInstance { get; set; }
        [DataMember(Name = "table", EmitDefaultValue = false)] public string Table { get; set; }
        /// <summary>A virtual table message, or the Custom API unique name.</summary>
        [DataMember(Name = "message", EmitDefaultValue = false)] public string Message { get; set; }
        [DataMember(Name = "caller", EmitDefaultValue = false)] public CallerContext Caller { get; set; }
        [DataMember(Name = "query", EmitDefaultValue = false)] public CanonicalQuery Query { get; set; }
        /// <summary>Dataverse row GUID for Retrieve, Update and Delete.</summary>
        [DataMember(Name = "id", EmitDefaultValue = false)] public string Id { get; set; }
        /// <summary>Create: all supplied columns. Update: changed columns only.</summary>
        [DataMember(Name = "record", EmitDefaultValue = false)] public List<Cell> Record { get; set; }

        /// <summary>Custom API input parameters, one cell per parameter with the name in <see cref="Cell.Col"/> (D-18).</summary>
        [DataMember(Name = "inputs", EmitDefaultValue = false)] public List<Cell> Inputs { get; set; }
    }

    [DataContract]
    public sealed class CallerContext
    {
        [DataMember(Name = "userObjectId", EmitDefaultValue = false)] public string UserObjectId { get; set; }
        [DataMember(Name = "async")] public bool Async { get; set; }
    }

    [DataContract]
    public sealed class CanonicalQuery
    {
        [DataMember(Name = "columns", EmitDefaultValue = false)] public List<string> Columns { get; set; }
        [DataMember(Name = "filter", EmitDefaultValue = false)] public FilterGroup Filter { get; set; }
        [DataMember(Name = "orderBy", EmitDefaultValue = false)] public List<OrderBy> OrderBy { get; set; }
        [DataMember(Name = "page", EmitDefaultValue = false)] public PageRequest Page { get; set; }
    }

    /// <summary>A filter group: <see cref="Type"/> is "and" or "or".</summary>
    [DataContract]
    public sealed class FilterGroup
    {
        [DataMember(Name = "type", EmitDefaultValue = false)] public string Type { get; set; }
        [DataMember(Name = "conditions", EmitDefaultValue = false)] public List<Condition> Conditions { get; set; }
        [DataMember(Name = "groups", EmitDefaultValue = false)] public List<FilterGroup> Groups { get; set; }
    }

    [DataContract]
    public sealed class Condition
    {
        [DataMember(Name = "col", EmitDefaultValue = false)] public string Col { get; set; }
        [DataMember(Name = "op", EmitDefaultValue = false)] public string Op { get; set; }
        [DataMember(Name = "values", EmitDefaultValue = false)] public List<string> Values { get; set; }
    }

    [DataContract]
    public sealed class OrderBy
    {
        [DataMember(Name = "col", EmitDefaultValue = false)] public string Col { get; set; }
        [DataMember(Name = "desc")] public bool Desc { get; set; }
    }

    [DataContract]
    public sealed class PageRequest
    {
        [DataMember(Name = "size")] public int Size { get; set; }
        /// <summary>The host's page token from the previous page's <see cref="CanonicalResponse.Next"/>; null for page 1.</summary>
        [DataMember(Name = "token", EmitDefaultValue = false)] public string Token { get; set; }
    }

    /// <summary>One column value. <see cref="Type"/> is a <see cref="CellTypes"/> value; <see cref="Value"/> is invariant (datetimes ISO 8601 UTC "o", option sets as the integer).</summary>
    [DataContract]
    public sealed class Cell
    {
        [DataMember(Name = "col", EmitDefaultValue = false)] public string Col { get; set; }
        [DataMember(Name = "type", EmitDefaultValue = false)] public string Type { get; set; }
        [DataMember(Name = "value", EmitDefaultValue = false)] public string Value { get; set; }
    }
}
