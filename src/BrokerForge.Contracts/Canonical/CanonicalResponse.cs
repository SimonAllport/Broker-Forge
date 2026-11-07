using System.Collections.Generic;
using System.Runtime.Serialization;

namespace BrokerForge.Contracts
{
    [DataContract]
    public sealed class Row
    {
        /// <summary>Encoded Dataverse GUID for the row.</summary>
        [DataMember(Name = "id", EmitDefaultValue = false)] public string Id { get; set; }
        [DataMember(Name = "cells", EmitDefaultValue = false)] public List<Cell> Cells { get; set; }
    }

    /// <summary>RetrieveMultiple: Rows and Next. Retrieve: Record. Create: Id. Custom API: Outputs.</summary>
    [DataContract]
    public sealed class CanonicalResponse
    {
        [DataMember(Name = "correlationId", EmitDefaultValue = false)] public string CorrelationId { get; set; }
        [DataMember(Name = "rows", EmitDefaultValue = false)] public List<Row> Rows { get; set; }
        /// <summary>Page token for the next page; null when this is the last page.</summary>
        [DataMember(Name = "next", EmitDefaultValue = false)] public string Next { get; set; }
        [DataMember(Name = "record", EmitDefaultValue = false)] public Row Record { get; set; }
        [DataMember(Name = "id", EmitDefaultValue = false)] public string Id { get; set; }

        /// <summary>Custom API output parameters, one cell per output.</summary>
        [DataMember(Name = "outputs", EmitDefaultValue = false)] public List<Cell> Outputs { get; set; }
    }

    /// <summary>Body of every non-200 reply. <see cref="Error"/> is the exception type name or the mapped kind.</summary>
    [DataContract]
    public sealed class ErrorPayload
    {
        [DataMember(Name = "correlationId", EmitDefaultValue = false)] public string CorrelationId { get; set; }
        [DataMember(Name = "error", EmitDefaultValue = false)] public string Error { get; set; }
        [DataMember(Name = "message", EmitDefaultValue = false)] public string Message { get; set; }
        [DataMember(Name = "connectUrl", EmitDefaultValue = false)] public string ConnectUrl { get; set; }
    }
}
