using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace BrokerForge.Schema;

/// <summary>Strict table schema parser (descriptor spec 14). Unknown keys are errors and every error names the YAML path.</summary>
public static partial class SchemaParser
{
    [GeneratedRegex(@"^[a-z][a-z0-9]*_[a-z0-9_]+$")]
    private static partial Regex LogicalNameRegex();

    [GeneratedRegex(@"^[a-z][a-z0-9]*_[A-Za-z][A-Za-z0-9_]*$")]
    private static partial Regex UniqueNameRegex();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*$")]
    private static partial Regex ParamNameRegex();

    public static SourceSchema Parse(string yaml, string source) => new Reader(source).Read(yaml);

    private sealed class Reader(string source)
    {
        private SchemaException Fail(string path, string message) => new(source, path, message);

        private static string Join(string path, string key) => path.Length == 0 ? key : path + "." + key;

        public SourceSchema Read(string yaml)
        {
            var stream = new YamlStream();
            try
            {
                stream.Load(new StringReader(yaml));
            }
            catch (YamlException ex)
            {
                throw Fail("", $"invalid YAML at line {ex.Start.Line}: {ex.Message}");
            }
            if (stream.Documents.Count != 1)
                throw Fail("", "a schema file holds exactly one YAML document");

            var root = Mapping(stream.Documents[0].RootNode, "");
            OnlyKeys(root, "", "table", "displayName", "displayCollectionName", "externalName", "primaryName", "columns", "customApis");

            var table = OptionalScalar(root, "", "table");
            string? displayName = null, displayCollectionName = null, externalName = null, primaryName = null;
            var columns = new Dictionary<string, ColumnSchema>();
            if (table != null)
            {
                if (!LogicalNameRegex().IsMatch(table))
                    throw Fail("table", $"`{table}` is not a logical name with a prefix, such as ext_order");
                displayName = RequiredScalar(root, "", "displayName");
                displayCollectionName = RequiredScalar(root, "", "displayCollectionName");
                externalName = RequiredScalar(root, "", "externalName");
                primaryName = RequiredScalar(root, "", "primaryName");
                foreach (var (k, v) in Mapping(Require(root, "", "columns"), "columns").Children)
                {
                    var name = Scalar(k, "columns");
                    columns[name] = ReadColumn(v, table, name, Join("columns", name));
                }
                if (!columns.TryGetValue(primaryName, out var primary) || primary.Type != "string")
                    throw Fail("primaryName", $"primaryName `{primaryName}` must name a string column");
            }
            else
            {
                foreach (var key in new[] { "displayName", "displayCollectionName", "externalName", "primaryName", "columns" })
                    if (Get(root, key) != null)
                        throw Fail(key, $"`{key}` describes a table; add `table` or remove it");
            }

            // One source is generated under one publisher, so every name shares one prefix.
            var prefix = table == null ? null : Prefix(table);
            var apis = new Dictionary<string, CustomApiSchema>();
            if (Get(root, "customApis") is { } apiNode)
                foreach (var (k, v) in Mapping(apiNode, "customApis").Children)
                {
                    var name = Scalar(k, "customApis");
                    var p = Join("customApis", name);
                    if (!UniqueNameRegex().IsMatch(name))
                        throw Fail(p, $"`{name}` is not a Custom API unique name with a prefix, such as ext_AdjustStock");
                    prefix ??= Prefix(name);
                    CheckPrefix(name, prefix, p);
                    apis[name] = ReadCustomApi(Mapping(v, p), p);
                }

            if (table == null && apis.Count == 0)
                throw Fail("", "a schema file declares a `table`, `customApis` or both");
            return new SourceSchema(table, displayName, displayCollectionName, externalName, primaryName, columns, apis);
        }

        private ColumnSchema ReadColumn(YamlNode node, string table, string name, string path)
        {
            if (!LogicalNameRegex().IsMatch(name))
                throw Fail(path, $"`{name}` is not a lower-case logical name with a prefix");
            CheckPrefix(name, Prefix(table), path);
            if (name == table + "id")
                throw Fail(path, $"`{name}` is the primary key, which is implicit");
            var m = Mapping(node, path);
            OnlyKeys(m, path, "type", "displayName", "maxLength", "precision", "options");
            var type = RequiredScalar(m, path, "type");
            if (type == "money")
                throw Fail(Join(path, "type"), "type money is not available: virtual tables cannot have Money columns (HLD D-15); use decimal");
            if (type == "guid")
                throw Fail(Join(path, "type"), "guid columns are not available: the primary key is the only GUID column");
            if (!ColumnSchema.Types.Contains(type))
                throw Fail(Join(path, "type"), $"type must be one of {string.Join(", ", ColumnSchema.Types)}; got `{type}`");

            int? maxLength = Get(m, "maxLength") is { } ml ? Int(ml, Join(path, "maxLength")) : null;
            int? precision = Get(m, "precision") is { } pr ? Int(pr, Join(path, "precision")) : null;
            Dictionary<string, int>? options = null;
            if (Get(m, "options") is { } on)
            {
                options = new Dictionary<string, int>();
                foreach (var (ok, ov) in Mapping(on, Join(path, "options")).Children)
                {
                    var label = Scalar(ok, Join(path, "options"));
                    options[label] = Int(ov, Join(Join(path, "options"), label));
                }
                if (options.Count == 0)
                    throw Fail(Join(path, "options"), "an option set needs at least one option");
                if (options.Values.Distinct().Count() != options.Count)
                    throw Fail(Join(path, "options"), "two options share a value");
            }

            if ((type == "string") != (maxLength != null) && type != "memo")
                throw Fail(Join(path, "maxLength"), type == "string" ? "a string column needs maxLength" : $"maxLength is only for string and memo columns, not {type}");
            if ((type == "decimal") != (precision != null))
                throw Fail(Join(path, "precision"), type == "decimal" ? "a decimal column needs precision" : $"precision is only for decimal columns, not {type}");
            if (precision is < 0 or > 10)
                throw Fail(Join(path, "precision"), "precision must be from 0 to 10");
            if ((type == "optionset") != (options != null))
                throw Fail(Join(path, "options"), type == "optionset" ? "an optionset column needs options" : $"options are only for optionset columns, not {type}");
            return new ColumnSchema(type, RequiredScalar(m, path, "displayName"), maxLength, precision, options);
        }

        private CustomApiSchema ReadCustomApi(YamlMappingNode m, string path)
        {
            OnlyKeys(m, path, "displayName", "inputs", "outputs");
            var inputs = new Dictionary<string, ParamSchema>();
            if (Get(m, "inputs") is { } inNode)
                foreach (var (k, v) in Mapping(inNode, Join(path, "inputs")).Children)
                {
                    var name = Scalar(k, Join(path, "inputs"));
                    var p = Join(Join(path, "inputs"), name);
                    CheckParamName(name, p);
                    var pm = Mapping(v, p);
                    OnlyKeys(pm, p, "type", "required");
                    inputs[name] = new ParamSchema(ParamType(RequiredScalar(pm, p, "type"), Join(p, "type")),
                        Get(pm, "required") is { } r && Bool(r, Join(p, "required")));
                }
            var outputs = new Dictionary<string, string>();
            if (Get(m, "outputs") is { } outNode)
                foreach (var (k, v) in Mapping(outNode, Join(path, "outputs")).Children)
                {
                    var name = Scalar(k, Join(path, "outputs"));
                    var p = Join(Join(path, "outputs"), name);
                    CheckParamName(name, p);
                    outputs[name] = ParamType(Scalar(v, p), p);
                }
            return new CustomApiSchema(RequiredScalar(m, path, "displayName"), inputs, outputs);
        }

        private static string Prefix(string name) => name[..(name.IndexOf('_') + 1)];

        private void CheckPrefix(string name, string prefix, string path)
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
                throw Fail(path, $"`{name}` must use the prefix {prefix}, like every other name in this schema");
        }

        private void CheckParamName(string name, string path)
        {
            if (!ParamNameRegex().IsMatch(name))
                throw Fail(path, $"`{name}` is not a valid parameter name");
        }

        private string ParamType(string type, string path) =>
            CustomApiSchema.ParamTypes.Contains(type)
                ? type
                : throw Fail(path, $"Custom API parameter type must be one of {string.Join(", ", CustomApiSchema.ParamTypes)}; got `{type}`");

        // ---- YAML node helpers, as in DescriptorParser ----

        private YamlMappingNode Mapping(YamlNode node, string path) =>
            node as YamlMappingNode ?? throw Fail(path, "expected a mapping");

        private string Scalar(YamlNode node, string path) =>
            (node as YamlScalarNode)?.Value ?? throw Fail(path, "expected a scalar");

        private static YamlNode? Get(YamlMappingNode m, string key) =>
            m.Children.TryGetValue(new YamlScalarNode(key), out var v) ? v : null;

        private YamlNode Require(YamlMappingNode m, string path, string key) =>
            Get(m, key) ?? throw Fail(Join(path, key), $"`{key}` is required");

        private string RequiredScalar(YamlMappingNode m, string path, string key) => Scalar(Require(m, path, key), Join(path, key));

        private string? OptionalScalar(YamlMappingNode m, string path, string key) =>
            Get(m, key) is { } node ? Scalar(node, Join(path, key)) : null;

        private int Int(YamlNode node, string path) =>
            int.TryParse(Scalar(node, path), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v)
                ? v
                : throw Fail(path, "expected an integer");

        private bool Bool(YamlNode node, string path) => Scalar(node, path) switch
        {
            "true" => true,
            "false" => false,
            var other => throw Fail(path, $"expected true or false; got `{other}`"),
        };

        private void OnlyKeys(YamlMappingNode m, string path, params string[] allowed)
        {
            foreach (var k in m.Children.Keys)
            {
                var key = Scalar(k, path);
                if (!allowed.Contains(key))
                    throw Fail(Join(path, key), $"unknown key `{key}`; allowed here: {string.Join(", ", allowed)}");
            }
        }
    }
}
