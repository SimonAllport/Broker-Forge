using BrokerForge.Descriptors.Parsing;
using BrokerForge.Descriptors.Validation;
using BrokerForge.Schema;

namespace BrokerForge.Cli;

/// <summary>
/// <c>bf validate &lt;descriptorsRoot&gt; --schemas &lt;schemasRoot&gt;</c>: parses and validates every
/// <c>&lt;ServiceInstance&gt;/*.yaml</c> descriptor against <c>schemas/&lt;ServiceInstance&gt;.yaml</c>.
/// Prints one <c>&lt;file&gt;: rule &lt;n&gt; &lt;yamlPath&gt;: &lt;message&gt;</c> line per error; exit 1 on any error.
/// Reserved names here are only <c>Authorization</c>: the auth mode's own headers and query parameters
/// live in host settings, so the host checks those at startup. The two Custom API checks the host makes
/// across files run here too: a unique name bound in two instances, and a schema API no descriptor binds.
/// </summary>
public static class ValidateCommand
{
    public static int Run(string[] args, TextWriter output)
    {
        if (args.Length != 3 || args[1] != "--schemas")
        {
            output.WriteLine("usage: bf validate <descriptorsRoot> --schemas <schemasRoot>");
            return 2;
        }
        var descriptorsRoot = Path.GetFullPath(args[0]);
        var schemasRoot = Path.GetFullPath(args[2]);
        if (!Directory.Exists(descriptorsRoot))
        {
            output.WriteLine($"{args[0]}: no such directory");
            return 1;
        }

        var errors = 0;
        var files = 0;
        void Report(string file, int rule, string path, string message)
        {
            output.WriteLine($"{file}: rule {rule} {(path.Length == 0 ? "(root)" : path)}: {message}");
            errors++;
        }

        // Custom API names are global in Dataverse: unique name -> the descriptor that binds it.
        var customApis = new Dictionary<string, (string Instance, string File)>();
        foreach (var instanceDir in Directory.GetDirectories(descriptorsRoot).Order(StringComparer.Ordinal))
        {
            var instance = Path.GetFileName(instanceDir);
            var schemaFile = Path.Combine(schemasRoot, instance + ".yaml");
            var schemaName = Path.GetRelativePath(Directory.GetCurrentDirectory(), schemaFile);
            SourceSchema? schema = null;
            if (!File.Exists(schemaFile))
                Report(schemaName, 7, "", $"no table schema for service instance {instance}");
            else
                try
                {
                    schema = SchemaParser.Parse(File.ReadAllText(schemaFile), schemaName);
                }
                catch (SchemaException ex)
                {
                    Report(schemaName, 1, ex.YamlPath, ex.Detail);
                }

            var operations = new Dictionary<string, string>();
            foreach (var file in Directory.GetFiles(instanceDir, "*.yaml").Order(StringComparer.Ordinal))
            {
                files++;
                var name = Path.GetRelativePath(descriptorsRoot, file);
                try
                {
                    var d = DescriptorParser.Parse(File.ReadAllText(file), name);
                    if (operations.TryGetValue(d.Operation, out var other))
                        Report(name, 1, "operation", $"operation `{d.Operation}` is also declared in {other}");
                    operations[d.Operation] = name;
                    if (Path.GetFileNameWithoutExtension(file) != d.Operation)
                        Report(name, 1, "operation", $"operation `{d.Operation}` must be in {d.Operation}.yaml");
                    foreach (var e in DescriptorValidator.Validate(d, ReservedNames.For(Array.Empty<string>()), schema))
                        Report(name, e.Rule, e.YamlPath, e.Message);
                    if (d.CustomApi is { } api)
                    {
                        if (customApis.TryGetValue(api, out var first))
                            Report(name, 7, "binds", $"Custom API {api} is also bound by {first.File} in {first.Instance}; unique names are global in Dataverse");
                        else
                            customApis[api] = (instance, name);
                    }
                }
                catch (DescriptorException ex)
                {
                    Report(name, 1, ex.YamlPath, ex.Detail);
                }
            }
            foreach (var api in schema?.CustomApis.Keys ?? Enumerable.Empty<string>())
                if (!(customApis.TryGetValue(api, out var bound) && bound.Instance == instance))
                    Report(schemaName, 7, "customApis." + api, $"Custom API {api} is declared, but no {instance} descriptor binds it");
        }

        output.WriteLine(errors == 0 ? $"{files} descriptors valid" : $"{errors} error(s) in {files} descriptors");
        return errors == 0 ? 0 : 1;
    }
}
