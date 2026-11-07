using System.Text.Json.Nodes;
using BrokerForge.Contracts;

namespace BrokerForge.Contract.Tests;

/// <summary>
/// One test per descriptor spec section 3-4 feature, through the real host against the mock's echo
/// route, asserting on what the source received (the testing rules).
/// </summary>
[Collection(MockApiCollection.Name)]
public class DescriptorFeatureTests(MockApiFixture mock) : HostTestBase(mock)
{
    private async Task<JsonNode> CreateEcho(params Cell[] cells)
    {
        var request = HostFactory.Request("ext_echo", Messages.Create, "EchoAPI");
        request.Record = cells.ToList();
        var (status, _, error) = await Host.Execute(request);
        Assert.True(status == 200, error?.Message);
        return await LastEcho();
    }

    private static readonly Cell[] Basic =
    {
        Cell("ext_name", "MiXeD Name"), Cell("ext_count", "3", CellTypes.Int), Cell("ext_lines_json", """[{"sku":"A"}]"""),
        Cell("ext_when", "2026-10-31T09:05:01.0000000Z", CellTypes.DateTime),
    };

    private static string H(JsonNode echo, string name) => echo["headers"]![name]!.GetValue<string>();

    [Fact]
    public async Task Binding_StaticInstanceContextComputed_Sent()
    {
        var echo = await CreateEcho(Basic);
        Assert.Equal("2024-06", H(echo, "x-static"));
        Assert.Equal("eu", H(echo, "x-instance"));
        Assert.Equal(HostFactory.Environment1, H(echo, "x-env"));
        Assert.Equal("user-1", H(echo, "x-user"));
        Assert.True(Guid.TryParse(H(echo, "x-uuid"), out _));
        Assert.EndsWith("Z", H(echo, "x-now"));
        Assert.True(Guid.TryParse(H(echo, "x-corr"), out _));
        Assert.Equal("/v2/echo/json/acc-9", echo["path"]!.GetValue<string>());
    }

    [Fact]
    public async Task Binding_SecretInHeader_SentAndAbsentFromLogs()
    {
        var echo = await CreateEcho(Basic);
        Assert.Equal(HostFactory.PartnerSecret, H(echo, "x-partner"));
        Assert.Equal(MockApiFixture.ApiKey, "bf-test-dummy-key");
        Assert.NotEmpty(Host.Logs);
        Assert.DoesNotContain(Host.Logs, line => line.Contains(HostFactory.PartnerSecret) || line.Contains(MockApiFixture.ApiKey));
    }

    [Fact]
    public async Task Logs_NeverContainQuerySecret()
    {
        var request = HostFactory.Request("ext_echoq", Messages.Retrieve, "EchoAPI");
        request.Id = Id("x");
        var (status, _, error) = await Host.Execute(request);
        Assert.True(status == 200, error?.Message);
        Assert.Equal("k=" + HostFactory.PartnerSecret, (await LastEcho())["rawQuery"]!.GetValue<string>());
        Assert.NotEmpty(Host.Logs);
        Assert.DoesNotContain(Host.Logs, line => line.Contains(HostFactory.PartnerSecret));
    }

    [Fact]
    public async Task ErrorText_RedactsHeaderSecrets()
    {
        var request = HostFactory.Request("ext_echofail", Messages.Retrieve, "EchoAPI");
        request.Id = Id("x");
        var (status, _, error) = await Host.Execute(request);
        Assert.Equal(502, status);
        Assert.Equal("SourceException", error!.Error);
        Assert.Contains("x-partner", error.Message);
        Assert.DoesNotContain(HostFactory.PartnerSecret, error.Message);
        Assert.DoesNotContain(Host.Logs, line => line.Contains(HostFactory.PartnerSecret));
    }

    [Fact]
    public async Task Logs_CarryCorrelationId()
    {
        var request = HostFactory.Request("ext_echo", Messages.Create, "EchoAPI");
        request.Record = Basic.ToList();
        await Host.Execute(request);
        Assert.Contains(Host.Logs, line => line.Contains("correlationId=" + request.CorrelationId));
    }

    [Fact]
    public async Task PathParams_UrlEncoded()
    {
        var request = HostFactory.Request("ext_echo", Messages.Retrieve, "EchoAPI");
        request.Id = Id("a/b c&d");
        var (status, response, error) = await Host.Execute(request);
        Assert.True(status == 200, error?.Message);
        Assert.Equal("/v2/echo/orders/a%2Fb%20c%26d", (await LastEcho())["path"]!.GetValue<string>());
        Assert.Equal("/v2/echo/orders/a%2Fb%20c%26d", Value(response!.Record, "ext_name"));
    }

    [Fact]
    public async Task Query_OmitIfNull()
    {
        var echo = await CreateEcho(Basic);
        Assert.Equal("b=", echo["rawQuery"]!.GetValue<string>());
        var withA = await CreateEcho(Basic.Append(Cell("ext_a", "x y")).ToArray());
        Assert.Equal("a=x%20y&b=", withA["rawQuery"]!.GetValue<string>());
    }

    [Fact]
    public async Task Query_InCsv()
    {
        var or = new FilterGroup { Type = "and", Conditions = new() { new Condition { Col = "ext_status", Op = Ops.In, Values = new() { "open", "shipped" } } }, Groups = new() };
        var (status, response, error) = await Host.Execute(List("ext_echo", 10, or, instance: "EchoAPI"));
        Assert.True(status == 200, error?.Message);
        Assert.Empty(response!.Rows);
        Assert.Equal("status=open%2Cshipped", (await LastEcho())["rawQuery"]!.GetValue<string>());
    }

    [Fact]
    public async Task Body_Template()
    {
        var echo = await CreateEcho(Basic);
        Assert.StartsWith("application/json", echo["contentType"]!.GetValue<string>());
        var body = JsonNode.Parse(echo["body"]!.GetValue<string>())!;
        Assert.Equal("MiXeD Name", body["name"]!.GetValue<string>());
        Assert.Equal(3, body["nested"]!["count"]!.GetValue<int>());
        Assert.True(body["flag"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Body_FormatJson()
    {
        var body = JsonNode.Parse((await CreateEcho(Basic))["body"]!.GetValue<string>())!;
        Assert.Equal("A", body["lines"]![0]!["sku"]!.GetValue<string>());

        var request = HostFactory.Request("ext_echo", Messages.Create, "EchoAPI");
        request.Record = new() { Cell("ext_name", "n"), Cell("ext_lines_json", "{bad"), Cell("ext_when", "2026-10-31T00:00:00Z") };
        var (status, _, error) = await Host.Execute(request);
        Assert.Equal(502, status);
        Assert.Equal("MappingException", error!.Error);
    }

    [Fact]
    public async Task Body_RawXml()
    {
        var request = HostFactory.Request("ext_echoxml", Messages.Create, "EchoAPI");
        request.Record = new() { Cell("ext_name", "<A & B>") };
        Assert.Equal(200, (await Host.Execute(request)).Status);
        var echo = await LastEcho();
        Assert.StartsWith("text/xml", echo["contentType"]!.GetValue<string>());
        Assert.Equal($"<order><name>&lt;A &amp; B&gt;</name><corr>{request.CorrelationId}</corr></order>", echo["body"]!.GetValue<string>());
    }

    [Fact]
    public async Task Body_FormUrlEncoded()
    {
        var request = HostFactory.Request("ext_echoform", Messages.Create, "EchoAPI");
        request.Record = new() { Cell("ext_name", "a b&c") };
        Assert.Equal(200, (await Host.Execute(request)).Status);
        var echo = await LastEcho();
        Assert.StartsWith("application/x-www-form-urlencoded", echo["contentType"]!.GetValue<string>());
        Assert.Equal("name=a+b%26c&kind=order", echo["body"]!.GetValue<string>());
    }

    [Fact]
    public async Task Update_ChangedColumnsOnly()
    {
        var request = HostFactory.Request("ext_echo", Messages.Update, "EchoAPI");
        request.Id = Id("e1");
        request.Record = new() { Cell("ext_ref", "R1") };
        var (status, _, error) = await Host.Execute(request);
        Assert.True(status == 200, error?.Message);
        var echo = await LastEcho();
        Assert.Equal("PATCH", echo["method"]!.GetValue<string>());
        Assert.Equal("""{"ref":"R1"}""", echo["body"]!.GetValue<string>());
    }

    [Fact]
    public async Task Update_AlwaysSend()
    {
        var request = HostFactory.Request("ext_echo", Messages.Update, "EchoAPI");
        request.Id = Id("e1");
        request.Record = new() { Cell("ext_name", "only name") };
        var (status, _, error) = await Host.Execute(request);
        Assert.Equal(400, status);
        Assert.Equal("Validation", error!.Error);
        Assert.Contains("ext_ref", error.Message);
    }

    [Fact]
    public async Task Formats_AllDeclared()
    {
        var echo = await CreateEcho(Basic);
        Assert.Equal("2026-10-31T09:05:01.000Z", H(echo, "x-iso"));
        Assert.Equal("2026-10-31", H(echo, "x-date"));
        Assert.Equal(DateTimeOffset.Parse("2026-10-31T09:05:01Z").ToUnixTimeSeconds().ToString(), H(echo, "x-epoch"));
        Assert.Equal(DateTimeOffset.Parse("2026-10-31T09:05:01Z").ToUnixTimeMilliseconds().ToString(), H(echo, "x-millis"));
        Assert.Equal("mixed name", H(echo, "x-lower"));
        Assert.Equal("MIXED NAME", H(echo, "x-upper"));
    }
}
