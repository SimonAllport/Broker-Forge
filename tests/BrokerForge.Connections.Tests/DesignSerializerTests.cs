using BrokerForge.Connections;

namespace BrokerForge.Connections.Tests;

public class DesignSerializerTests
{
    internal static ConnectionDesign Sample(string openApi = "openapi: 3.0.0") => new(
        1, "OrdersAPI", 3, "rest", openApi,
        new Dictionary<string, string> { ["b"] = "2", ["a"] = "1", ["B"] = "3" },
        new DesignAuth("Static", "ApiKeyHeader", "X-Api-Key", null, null, null, null, null, null, ["apiKey"]),
        new DesignObjectMap("ext_orders", "reversible", "guid", new Dictionary<string, string> { ["list"] = "ListOrders", ["get"] = "GetOrder" }),
        "table: ext_orders\n",
        new Dictionary<string, string> { ["ListOrders"] = "name: ListOrders\n", ["GetOrder"] = "name: GetOrder\n" });

    [Fact]
    public void RoundTripIsByteStable()
    {
        var first = DesignSerializer.Serialize(Sample());
        var second = DesignSerializer.Serialize(DesignSerializer.Parse(first, "t"));
        Assert.Equal(first, second);
    }

    [Fact]
    public void DictionariesAreSortedOrdinally()
    {
        var json = DesignSerializer.Serialize(Sample());
        Assert.True(json.IndexOf("\"B\":", StringComparison.Ordinal) < json.IndexOf("\"a\":", StringComparison.Ordinal));
        Assert.True(json.IndexOf("\"a\":", StringComparison.Ordinal) < json.IndexOf("\"b\":", StringComparison.Ordinal));
        Assert.True(json.IndexOf("\"GetOrder\":", StringComparison.Ordinal) < json.IndexOf("\"ListOrders\":", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownKeyFails()
    {
        var json = DesignSerializer.Serialize(Sample()).Replace("\"mode\": \"Static\"", "\"mode\": \"Static\", \"colour\": \"red\"");
        var e = Assert.Throws<DesignFormatException>(() => DesignSerializer.Parse(json, "design.xml"));
        Assert.Contains("$.auth.colour", e.Message);
        Assert.Contains("design.xml", e.Message);
    }

    [Fact]
    public void FormatVersion2Fails()
    {
        var json = DesignSerializer.Serialize(Sample()).Replace("\"formatVersion\": 1", "\"formatVersion\": 2");
        var e = Assert.Throws<DesignFormatException>(() => DesignSerializer.Parse(json, "design.xml"));
        Assert.Contains("$.formatVersion", e.Message);
    }

    [Fact]
    public void AuthWithSecretValueFails()
    {
        var e = Assert.Throws<DesignFormatException>(() => DesignSerializer.Parse("{\"auth\":{\"clientSecret\":\"x\"}}", "design.xml"));
        Assert.Contains("$.auth.clientSecret", e.Message);
        Assert.DoesNotContain("\"x\"", e.Message);
    }

    [Fact]
    public void MissingInstanceFails()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(DesignSerializer.Serialize(Sample()))!.AsObject();
        node.Remove("instance");
        var e = Assert.Throws<DesignFormatException>(() => DesignSerializer.Parse(node.ToJsonString(), "design.xml"));
        Assert.Contains("$.instance", e.Message);
        Assert.Contains("missing", e.Message);
    }

    [Fact]
    public void OAuthUrlsAreOptional()
    {
        var d = Sample() with
        {
            Auth = Sample().Auth with { Mode = "Impersonate", OAuth = new DesignOAuth(null, null, new[] { "api://contoso/.default" }, null, true) },
        };
        var json = DesignSerializer.Serialize(d);
        Assert.DoesNotContain("authorizeUrl", json);
        Assert.DoesNotContain("tokenUrl", json);
        var back = DesignSerializer.Parse(json, "design.xml");
        Assert.Null(back.Auth.OAuth!.AuthorizeUrl);
        Assert.Null(back.Auth.OAuth.TokenUrl);
        Assert.Equal(json, DesignSerializer.Serialize(back));
    }

    [Fact]
    public void WrapUnwrapRoundTrip()
    {
        var json = DesignSerializer.Serialize(Sample());
        Assert.Equal(json, DesignCarrier.Unwrap(DesignCarrier.Wrap(json), "t"));
    }

    [Theory]
    [InlineData("<brokerforgeDesign formatVersion=\"1\"><![CDATA[{}]]><extra/></brokerforgeDesign>")]
    [InlineData("<brokerforgeDesign formatVersion=\"1\">{}</brokerforgeDesign>")]
    [InlineData("<brokerforgeDesign formatVersion=\"1\"></brokerforgeDesign>")]
    [InlineData("<brokerforgeDesign formatVersion=\"2\"><![CDATA[{}]]></brokerforgeDesign>")]
    [InlineData("<brokerforgeDesign formatVersion=\"1\" other=\"x\"><![CDATA[{}]]></brokerforgeDesign>")]
    [InlineData("<other formatVersion=\"1\"><![CDATA[{}]]></other>")]
    public void UnwrapRejectsExtraNodes(string xml)
    {
        var e = Assert.Throws<DesignFormatException>(() => DesignCarrier.Unwrap(xml, "design.xml"));
        Assert.Contains("design.xml", e.Message);
    }

    [Theory]
    [InlineData("<!DOCTYPE brokerforgeDesign [<!ENTITY x \"y\">]><brokerforgeDesign formatVersion=\"1\"><![CDATA[{}]]></brokerforgeDesign>")]
    [InlineData("<!DOCTYPE brokerforgeDesign SYSTEM \"http://contoso.example/x.dtd\"><brokerforgeDesign formatVersion=\"1\"><![CDATA[{}]]></brokerforgeDesign>")]
    public void UnwrapRefusesDtd(string xml)
    {
        var e = Assert.Throws<DesignFormatException>(() => DesignCarrier.Unwrap(xml, "design.xml"));
        Assert.Contains("design.xml", e.Message);
        Assert.Contains("DTD", e.Message);
    }

    [Fact]
    public void TextContainingCdataEndSurvives()
    {
        var text = "description: 'a ]]> b'\nexample: \"]]>]]>\"";
        var d = Sample(text);
        var json = DesignSerializer.Serialize(d);
        Assert.DoesNotContain("]]>", json);
        var back = DesignSerializer.Parse(DesignCarrier.Unwrap(DesignCarrier.Wrap(json), "t"), "t");
        Assert.Equal(text, back.OpenApi);
        Assert.Equal(json, DesignSerializer.Serialize(back));
    }

    [Fact]
    public void SerializedOutputEscapesCdataEnd()
    {
        var json = DesignSerializer.Serialize(Sample("a ]]> b"));
        Assert.Contains("]]\\u003E", json);
        Assert.DoesNotContain("]]>", json);
    }

    [Fact]
    public void DuplicateDescriptorKeyFails()
    {
        var json = DesignSerializer.Serialize(Sample()).Replace("\"GetOrder\": \"name: GetOrder\\n\",", "\"GetOrder\": \"name: GetOrder\\n\", \"GetOrder\": \"x\",");
        var e = Assert.Throws<DesignFormatException>(() => DesignSerializer.Parse(json, "design.xml"));
        Assert.Contains("$.descriptors.GetOrder", e.Message);
        Assert.Contains("design.xml", e.Message);
    }
}
