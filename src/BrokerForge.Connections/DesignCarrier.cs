using System.Xml;
using System.Xml.Linq;

namespace BrokerForge.Connections;

/// <summary>
/// The web resource body for a design: XML (web resource type 4) with the design JSON in one CDATA section. Serialize already rewrites "]]>" so the wrap is always safe.
/// </summary>
public static class DesignCarrier
{
    private const string Root = "brokerforgeDesign";

    public static string Wrap(string json)
    {
        if (json.Contains("]]>", StringComparison.Ordinal))
            throw new DesignFormatException("json contains ']]>' and cannot be wrapped; use DesignSerializer.Serialize");
        return $"<{Root} formatVersion=\"1\"><![CDATA[{json}]]></{Root}>";
    }

    public static string Unwrap(string xml, string source)
    {
        XDocument doc;
        // A web resource is written by whoever can customise the environment: no DTD, so no entity expansion or external fetch.
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            doc = XDocument.Load(reader);
        }
        catch (XmlException e) { throw new DesignFormatException($"{source}: not valid XML ({e.Message})"); }

        var root = doc.Root!;
        if (root.Name.LocalName != Root || root.Name.Namespace != XNamespace.None)
            throw new DesignFormatException($"{source}: root element must be <{Root}>");
        var attrs = root.Attributes().ToList();
        if (attrs.Count != 1 || attrs[0].Name != "formatVersion" || attrs[0].Value != "1")
            throw new DesignFormatException($"{source}: {Root} must have exactly the attribute formatVersion=\"1\"");
        var nodes = root.Nodes().ToList();
        if (nodes.Count != 1 || nodes[0] is not XCData cdata)
            throw new DesignFormatException($"{source}: {Root} must contain exactly one CDATA section and nothing else");
        return cdata.Value;
    }
}
