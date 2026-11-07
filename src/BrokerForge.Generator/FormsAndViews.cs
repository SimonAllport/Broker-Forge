using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using BrokerForge.Schema;

namespace BrokerForge.Generator;

/// <summary>The table's main form and default public view: the only forms and views the generator writes (generator spike section 1).</summary>
public sealed record GeneratedParts(string FormId, string FormXml, string ViewId, string FetchXml, string LayoutXml);

/// <summary>
/// Reads and rewrites the generated parts so that they show a given list of columns, primary name first. Only the
/// first section of the form, the view's attributes and order, and the grid row are replaced; everything else the
/// platform wrote stays, so the grid's object type code never has to be known.
/// </summary>
public static class FormsAndViews
{
    private static readonly IReadOnlyDictionary<string, string> ClassIds = new Dictionary<string, string>
    {
        ["string"] = "{4273EDBD-AC1D-40d3-9FB2-095C621B552D}",
        ["memo"] = "{E0DECE4B-6FC8-4a8f-A065-082708572369}",
        ["int"] = "{C6D124CA-7EDA-4a60-AEA9-7FB8D318B68F}",
        ["decimal"] = "{C3EFE0C3-0EC6-42be-8349-CBD9079DFD8E}",
        ["bool"] = "{67FAC785-CD58-4f9f-ABB3-4B7DDC6ED5ED}",
        ["datetime"] = "{5B773807-9FB2-42db-97C3-7A91EFF8ADFF}",
        ["date"] = "{5B773807-9FB2-42db-97C3-7A91EFF8ADFF}",
        ["optionset"] = "{3EF39988-22BB-4f0b-BBBE-64B5A3748AEE}",
    };

    /// <summary>The main form (type 2; the one called Information if there are several) and the default public view; null while the table has neither.</summary>
    public static async Task<GeneratedParts?> ReadAsync(DataverseClient client, string table, CancellationToken ct)
    {
        // systemform has no createdon to order by (live): the platform's own main form is called Information.
        var forms = await client.ListAsync($"systemforms?$select=formid,formxml,name&$filter=objecttypecode eq {DataverseClient.Literal(table)} and type eq 2", ct);
        var form = forms.FirstOrDefault(f => (string?)f["name"] == "Information") ?? forms.FirstOrDefault();
        var view = (await client.ListAsync($"savedqueries?$select=savedqueryid,fetchxml,layoutxml&$filter=returnedtypecode eq {DataverseClient.Literal(table)} and querytype eq 0 and isdefault eq true", ct)).FirstOrDefault();
        if (form == null || view == null)
            return null;
        return new GeneratedParts((string)form["formid"]!, (string)form["formxml"]!, (string)view["savedqueryid"]!, (string)view["fetchxml"]!, (string)view["layoutxml"]!);
    }

    /// <summary>The columns in the order the form and view show them: the primary name, then the rest in schema order.</summary>
    public static IReadOnlyList<string> Order(SourceSchema schema, IEnumerable<string> columns)
    {
        var set = columns.ToHashSet(StringComparer.Ordinal);
        return schema.Columns.Keys.Where(set.Contains).OrderBy(c => c == schema.PrimaryName ? 0 : 1).ToList();
    }

    /// <summary>The columns each part shows now: the form's first section, the view's grid cells.</summary>
    public static (IReadOnlyList<string> Form, IReadOnlyList<string> View) Shown(GeneratedParts parts)
    {
        var section = XElement.Parse(parts.FormXml).Descendants("section").First();
        var form = section.Descendants("control").Select(c => (string?)c.Attribute("datafieldname")).OfType<string>().ToList();
        var view = XElement.Parse(parts.LayoutXml).Descendants("cell").Select(c => (string?)c.Attribute("name")).OfType<string>().ToList();
        return (form, view);
    }

    public static string FormXml(string existing, string table, IReadOnlyList<(string Column, ColumnSchema Schema)> columns)
    {
        var root = XElement.Parse(existing);
        var section = root.Descendants("section").First();
        section.Element("rows")?.Remove();
        section.Add(new XElement("rows", columns.Select(c => new XElement("row",
            new XElement("cell", new XAttribute("id", "{" + StableId(table + "." + c.Column) + "}"),
                new XElement("labels", new XElement("label", new XAttribute("description", c.Schema.DisplayName), new XAttribute("languagecode", 1033))),
                new XElement("control", new XAttribute("id", c.Column), new XAttribute("classid", ClassIds[c.Schema.Type]), new XAttribute("datafieldname", c.Column)))))));
        return root.ToString(SaveOptions.DisableFormatting);
    }

    public static string FetchXml(string existing, string table, IReadOnlyList<string> columns)
    {
        var root = XElement.Parse(existing);
        var entity = root.Element("entity")!;
        entity.RemoveNodes();
        entity.Add(new XElement("attribute", new XAttribute("name", table + "id")));
        entity.Add(columns.Select(c => new XElement("attribute", new XAttribute("name", c))));
        // No <order>: a sort the list descriptor doesn't declare fails loudly (UnsupportedQueryException), and the
        // generator doesn't know which sorts are declared. Rows come in the source's own order (sortStable).
        return root.ToString(SaveOptions.DisableFormatting);
    }

    public static string LayoutXml(string existing, string table, IReadOnlyList<string> columns)
    {
        var root = XElement.Parse(existing);
        var row = root.Element("row")!;
        row.RemoveNodes();
        row.SetAttributeValue("id", table + "id");
        row.Add(columns.Select(c => new XElement("cell", new XAttribute("name", c), new XAttribute("width", 150))));
        return root.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// True when both parts already show <paramref name="columns"/> as the generator would write them: the same cells in
    /// the same order with the schema's labels and controls, so a rename alone still rewrites the form.
    /// </summary>
    public static bool Matches(SourceSchema schema, GeneratedParts parts, IReadOnlyList<string> columns)
    {
        static IEnumerable<string> Cells(string formXml) => XElement.Parse(formXml).Descendants("section").First().Descendants("cell")
            .Select(c => $"{(string?)c.Element("control")?.Attribute("datafieldname")}|{(string?)c.Element("labels")?.Element("label")?.Attribute("description")}|{(string?)c.Element("control")?.Attribute("classid")}");
        var wanted = FormXml(parts.FormXml, schema.Table!, columns.Select(c => (c, schema.Columns[c])).ToList());
        return Cells(parts.FormXml).SequenceEqual(Cells(wanted), StringComparer.OrdinalIgnoreCase) && Shown(parts).View.SequenceEqual(columns)
            && !XElement.Parse(parts.FetchXml).Descendants("order").Any();
    }

    /// <summary>Writes both parts to show <paramref name="columns"/> unless they already do, then publishes. Returns true when it wrote.</summary>
    public static async Task<bool> ShowAsync(DataverseClient client, SourceSchema schema, GeneratedParts parts, IReadOnlyList<string> columns, CancellationToken ct)
    {
        if (Matches(schema, parts, columns))
            return false;
        var table = schema.Table!;
        await client.WriteAsync(HttpMethod.Patch, $"systemforms({parts.FormId})",
            new JsonObject { ["formxml"] = FormXml(parts.FormXml, table, columns.Select(c => (c, schema.Columns[c])).ToList()) }, null, ct);
        await client.WriteAsync(HttpMethod.Patch, $"savedqueries({parts.ViewId})", new JsonObject
        {
            ["fetchxml"] = FetchXml(parts.FetchXml, table, columns),
            ["layoutxml"] = LayoutXml(parts.LayoutXml, table, columns),
        }, null, ct);
        await Generator.PublishAsync(client, table, ct);
        return true;
    }

    /// <summary>A cell ID that is the same on every run, so a re-run writes the same XML.</summary>
    private static Guid StableId(string key) => new(MD5.HashData(Encoding.UTF8.GetBytes(key)));
}
