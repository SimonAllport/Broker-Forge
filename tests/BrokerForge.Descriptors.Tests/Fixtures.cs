using BrokerForge.Descriptors.Model;
using BrokerForge.Descriptors.Parsing;
using BrokerForge.Descriptors.Validation;

namespace BrokerForge.Descriptors.Tests;

internal static class Fixtures
{
    public static string RepoRoot { get; } = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "BrokerForge.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("BrokerForge.slnx not found above the test directory.");
    }

    public static string OrdersPath(string operation) => Path.Combine(RepoRoot, "descriptors", "OrdersAPI", operation + ".yaml");

    public static string OrdersYaml(string operation) => File.ReadAllText(OrdersPath(operation));

    public static OperationDescriptor Orders(string operation) => DescriptorParser.Parse(OrdersYaml(operation), operation + ".yaml");

    public static readonly ReservedNames NoAuthNames = ReservedNames.For(Array.Empty<string>());

    /// <summary>Replaces one exact fragment, failing the test if the fragment is missing so a mutation cannot silently do nothing.</summary>
    public static string Mutate(string yaml, string from, string to)
    {
        Assert.Contains(from, yaml);
        return yaml.Replace(from, to);
    }
}
