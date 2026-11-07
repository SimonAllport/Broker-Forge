using BrokerForge.SwaggerForge;
using YamlDotNet.RepresentationModel;

namespace SwaggerForge.Tests;

public class YamlWriterTests
{
    [Theory]
    [InlineData("line one\nline two")]
    [InlineData("tab\there")]
    [InlineData("carriage\rreturn")]
    [InlineData("bell\u0007 and nul\u0000")]
    [InlineData("next line\u0085 and line separator\u2028")]
    [InlineData("delete\u007f")]
    [InlineData("quote \" and backslash \\")]
    public void Scalar_ControlCharacters_RoundTrip(string value)
    {
        var yaml = new YamlMap().Add("description", value).Add(value, "as a key").ToString();
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        Assert.Equal(value, ((YamlScalarNode)root.Children[new YamlScalarNode("description")]).Value);
        Assert.Equal("as a key", ((YamlScalarNode)root.Children[new YamlScalarNode(value)]).Value);
    }
}
