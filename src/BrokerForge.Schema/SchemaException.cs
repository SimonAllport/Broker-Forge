namespace BrokerForge.Schema;

/// <summary>A table schema file that cannot be loaded. Raised by the CLI, the host at startup and the generator.</summary>
public sealed class SchemaException(string source, string yamlPath, string message)
    : Exception($"{source}: {(yamlPath.Length == 0 ? "(root)" : yamlPath)}: {message}")
{
    public new string Source { get; } = source;
    public string YamlPath { get; } = yamlPath;
    public string Detail { get; } = message;
}
