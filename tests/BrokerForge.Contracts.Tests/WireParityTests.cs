using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BrokerForge.Contracts;

namespace BrokerForge.Contracts.Tests;

public class WireParityTests
{
    private static readonly JsonSerializerOptions Stj = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string WriteDcjs<T>(T value)
    {
        var serializer = new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
        using var ms = new MemoryStream();
        serializer.WriteObject(ms, value);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static T ReadDcjs<T>(string json)
    {
        var serializer = new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return (T)serializer.ReadObject(ms)!;
    }

    private static CanonicalRequest FullRequest() => new()
    {
        CorrelationId = "8c1f0000-0000-0000-0000-000000000001",
        EnvironmentId = "a4c0d8aa-0000-0000-0000-000000000001",
        ServiceInstance = "OrdersAPI",
        Table = "ext_order",
        Message = Messages.RetrieveMultiple,
        Caller = new CallerContext { UserObjectId = "e7a0", Async = true },
        Query = new CanonicalQuery
        {
            Columns = new List<string> { "ext_name", "ext_status" },
            Filter = new FilterGroup
            {
                Type = "and",
                Conditions = new List<Condition> { new() { Col = "ext_status", Op = Ops.In, Values = new List<string> { "100000000", "100000001" } } },
                Groups = new List<FilterGroup> { new() { Type = "or", Conditions = new List<Condition> { new() { Col = "ext_customerref", Op = Ops.Eq, Values = new List<string> { "CUST-01" } } }, Groups = new List<FilterGroup>() } },
            },
            OrderBy = new List<OrderBy> { new() { Col = "ext_name", Desc = true } },
            Page = new PageRequest { Size = 50, Token = "tok" },
        },
        Id = "00000000-0000-0000-0000-000000000001",
        Record = new List<Cell> { new() { Col = "ext_customerref", Type = CellTypes.String, Value = "CUST-01" } },
    };

    [Fact]
    public void Request_DataContractJsonAndSystemTextJson_ProduceSameJson()
    {
        var request = FullRequest();
        var dcjs = JsonNode.Parse(WriteDcjs(request));
        var stj = JsonNode.Parse(JsonSerializer.Serialize(request, Stj));
        Assert.True(JsonNode.DeepEquals(dcjs, stj), $"DCJS: {dcjs?.ToJsonString()}\nSTJ:  {stj?.ToJsonString()}");
    }

    [Fact]
    public void Response_RoundTripsAcrossSerialisers()
    {
        var response = new CanonicalResponse
        {
            CorrelationId = "c1",
            Rows = new List<Row> { new() { Id = "g1", Cells = new List<Cell> { new() { Col = "ext_name", Type = CellTypes.String, Value = "REF-0001" } } } },
            Next = "next-token",
            Record = new Row { Id = "g2", Cells = new List<Cell> { new() { Col = "ext_placed", Type = CellTypes.DateTime, Value = "2026-10-31T00:00:00.0000000Z" } } },
            Id = "g3",
        };
        var back = ReadDcjs<CanonicalResponse>(JsonSerializer.Serialize(response, Stj));
        Assert.Equal(JsonSerializer.Serialize(response, Stj), JsonSerializer.Serialize(back, Stj));

        var error = new ErrorPayload { CorrelationId = "c1", Error = "SourceException", Message = "m", ConnectUrl = "https://x" };
        var errorBack = ReadDcjs<ErrorPayload>(JsonSerializer.Serialize(error, Stj));
        Assert.Equal(JsonSerializer.Serialize(error, Stj), JsonSerializer.Serialize(errorBack, Stj));
    }

    [Fact]
    public void Wire_InputsAndOutputs_DcjsStjParity()
    {
        var request = new CanonicalRequest
        {
            CorrelationId = "c1",
            EnvironmentId = "e1",
            Message = "ext_AdjustStock",
            Caller = new CallerContext { UserObjectId = "u1" },
            Inputs = new List<Cell> { new() { Col = "ItemId", Type = CellTypes.Int, Value = "7" }, new() { Col = "Reason", Type = CellTypes.String, Value = null } },
        };
        var dcjs = JsonNode.Parse(WriteDcjs(request));
        var stj = JsonNode.Parse(JsonSerializer.Serialize(request, Stj));
        Assert.True(JsonNode.DeepEquals(dcjs, stj), $"DCJS: {dcjs?.ToJsonString()}\nSTJ:  {stj?.ToJsonString()}");
        Assert.Equal("7", stj!["inputs"]![0]!["value"]!.GetValue<string>());
        Assert.Null(stj["serviceInstance"]);
        Assert.Null(stj["table"]);

        var response = new CanonicalResponse { CorrelationId = "c1", Outputs = new List<Cell> { new() { Col = "OnHand", Type = CellTypes.Int, Value = "12" } } };
        var back = ReadDcjs<CanonicalResponse>(JsonSerializer.Serialize(response, Stj));
        Assert.Equal("OnHand", back.Outputs.Single().Col);
        Assert.Equal(JsonSerializer.Serialize(response, Stj), JsonSerializer.Serialize(back, Stj));
    }

    [Fact]
    public void EveryDataMemberName_IsCamelCaseOfProperty()
    {
        var types = typeof(CanonicalRequest).Assembly.GetTypes().Where(t => t.GetCustomAttribute<DataContractAttribute>() != null).ToList();
        Assert.NotEmpty(types);
        foreach (var type in types)
            foreach (var prop in type.GetProperties())
            {
                var member = prop.GetCustomAttribute<DataMemberAttribute>();
                Assert.True(member != null, $"{type.Name}.{prop.Name} has no [DataMember]");
                Assert.Equal(JsonNamingPolicy.CamelCase.ConvertName(prop.Name), member!.Name);
            }
    }

    [Fact]
    public void Exceptions_CarryCorrelationId()
    {
        BrokerForgeException[] all =
        {
            new UnsupportedQueryException("m", "c"), new ConnectionRequiredException("m", "c", "https://connect"),
            new SourceException("m", "c", 500), new SourceTimeoutException("m", "c"), new MappingException("m", "c"),
            new BrokerUnavailableException("m", "c"), new MappedSourceException(SourceErrorKinds.NotFound, "m", "c", 404),
        };
        Assert.All(all, e => Assert.Equal("c", e.CorrelationId));
        Assert.Equal(500, ((SourceException)all[2]).Status);
        Assert.Equal("https://connect", ((ConnectionRequiredException)all[1]).ConnectUrl);
    }
}
