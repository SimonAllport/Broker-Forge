namespace BrokerForge.Descriptors.Parsing;

/// <summary>A descriptor that cannot be loaded. Raised at startup or by the CI validator, never per request.</summary>
public sealed class DescriptorException(string source, string yamlPath, string message)
    : Exception($"{source}: {(yamlPath.Length == 0 ? "(root)" : yamlPath)}: {message}")
{
    public new string Source { get; } = source;
    public string YamlPath { get; } = yamlPath;
    public string Detail { get; } = message;
}
