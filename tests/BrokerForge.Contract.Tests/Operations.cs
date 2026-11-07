using BrokerForge.Abstractions;
using BrokerForge.Adapters.Rest;
using BrokerForge.Auth;
using BrokerForge.Contracts;
using BrokerForge.Descriptors.Model;
using BrokerForge.Descriptors.Parsing;
using BrokerForge.Descriptors.Rendering;
using BrokerForge.Descriptors.Validation;
using BrokerForge.Host.Execution;
using BrokerForge.Keys;

namespace BrokerForge.Contract.Tests;

/// <summary>Builds operation contexts against the mock API for engine-level contract tests.</summary>
internal static class Operations
{
    public sealed class MockKey : ISecretResolver
    {
        public Task<string> GetAsync(SecretRef reference, CancellationToken ct) => Task.FromResult(MockApiFixture.ApiKey);
    }

    public static ServiceInstance Instance(string baseUrl) => new("OrdersAPI", "rest", baseUrl,
        new AuthConfig(AuthMode.Static, "apiKeyHeader", HeaderName: "X-Api-Key", SecretRef: "env:UNUSED"),
        new Dictionary<string, string> { ["accountId"] = "acc-1" }, new Dictionary<string, string> { ["partnerKey"] = "env:UNUSED" });

    public static string DescriptorPath(string name) =>
        File.Exists(Path.Combine(MockApiFixture.RepoRoot, "descriptors", "OrdersAPI", name + ".yaml"))
            ? Path.Combine(MockApiFixture.RepoRoot, "descriptors", "OrdersAPI", name + ".yaml")
            : Path.Combine(MockApiFixture.RepoRoot, "tests", "BrokerForge.Contract.Tests", "descriptors", name + ".yaml");

    public static OperationDescriptor Load(string name, Func<string, string>? mutate = null)
    {
        var yaml = File.ReadAllText(DescriptorPath(name));
        var d = DescriptorParser.Parse(mutate == null ? yaml : mutate(yaml), name + ".yaml");
        var errors = DescriptorValidator.Validate(d, ReservedNames.For(new[] { "X-Api-Key" }));
        Assert.True(errors.Count == 0, string.Join("; ", errors));
        return d;
    }

    public static async Task<OperationContext> Context(string baseUrl, OperationDescriptor d)
    {
        var instance = Instance(baseUrl);
        var channel = await new StaticAuthProvider(new MockKey(), new HttpClient()).AuthenticateAsync(instance, new CallerContext(), "corr-1", CancellationToken.None);
        var map = new ObjectMap("OrdersAPI", "ext_order", "reversible", "String", new Dictionary<string, string>());
        var binding = new BindingContext(instance, "corr-1", "env-1", null, false, null, null, null, null, null);
        return new OperationContext(instance, map, d, new ReversibleKeyStrategy(KeyKind.String), channel, binding);
    }

    public static readonly byte[] TokenKey = System.Text.Encoding.UTF8.GetBytes("bf-test-page-token-key-0123456789abcdef");

    public static PagingCoordinator Coordinator() => new(new OperationExecutor(new RestBroker()), new MockKey(), TokenKey);
}
