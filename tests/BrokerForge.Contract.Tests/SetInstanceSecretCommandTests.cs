using BrokerForge.Abstractions;
using BrokerForge.Auth;
using BrokerForge.Auth.Credentials;
using Azure;
using BrokerForge.Connections;
using BrokerForge.Host.Admin;
using BrokerForge.Host.Config;

namespace BrokerForge.Contract.Tests;

/// <summary>The one-off admin command that sets an instance secret (tools/dev/set-instance-secret.sh): it reads the value from a reference and never prints it.</summary>
public class SetInstanceSecretCommandTests
{
    private const string Env = "00000000-0000-0000-0000-00000000000a";
    private const string Value = "test-only-not-a-real-key-7f3a";

    private sealed class FakeResolver(string? value) : ISecretResolver
    {
        public int Reads;

        public Task<string> GetAsync(SecretRef reference, CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            return value is null ? throw new CredentialUnavailableException($"Key Vault returned 404 for {reference}.") : Task.FromResult(value);
        }
    }

    /// <summary>A table store that refuses every write (or read-back) the way Azure Tables does, with the value in its message.</summary>
    private sealed class RefusingStore(bool onRead) : IInstanceSecretStore
    {
        private static RequestFailedException Refused() =>
            new(403, "This request is not authorized to perform this operation. RequestId:0 Content: " + Value, "AuthorizationPermissionMismatch", null);
        public Task<string?> GetAsync(string environmentId, string instance, string name, CancellationToken ct) => throw Refused();
        public Task SetAsync(string environmentId, string instance, string name, string value, CancellationToken ct) =>
            onRead ? Task.CompletedTask : throw Refused();
        public Task<bool> ExistsAsync(string environmentId, string instance, string name, CancellationToken ct) => throw Refused();
        public Task DeleteAsync(string environmentId, string instance, string name, CancellationToken ct) => throw Refused();
    }

    /// <summary>Environment <see cref="Env"/> has the WarehouseAPI design, which lists the instance secret apiKey.</summary>
    private static EnvironmentConfigTests.FakeDesigns Designs()
    {
        var designs = new EnvironmentConfigTests.FakeDesigns();
        designs.Add(Env, EnvironmentConfigTests.Warehouse(), new EnvironmentSettings("https://inv.contoso.example", new Dictionary<string, string>()));
        return designs;
    }

    private static CompositeConfigProvider Provider(IDesignSource designs) =>
        new(new HostConfigStore(new ConfigInput()), designs, TimeProvider.System, TimeSpan.FromMinutes(5));

    private static async Task<(int Code, string Output, IInstanceSecretStore Store, FakeResolver Resolver)> Run(string? value, string[] args,
        IInstanceSecretStore? store = null, IDesignSource? designs = null)
    {
        store ??= new InMemoryInstanceSecretStore();
        var resolver = new FakeResolver(value);
        var output = new StringWriter();
        var code = await SetInstanceSecretCommand.RunAsync(args, [Env], Provider(designs ?? Designs()), store, resolver, output, CancellationToken.None);
        return (code, output.ToString(), store, resolver);
    }

    private static string[] Args(string instance = "WarehouseAPI", string name = "apiKey") =>
        ["--environment", Env, "--instance", instance, "--name", name, "--from", "kv:https://v.vault.azure.net/secrets/k"];

    [Fact]
    public void SplitTakesTheCommandOffTheHostArguments()
    {
        Assert.Empty(SetInstanceSecretCommand.Split(["set-instance-secret", "--name", "apiKey"], out var command));
        Assert.Equal(["--name", "apiKey"], command!);
        Assert.Equal(["--urls", "x"], SetInstanceSecretCommand.Split(["--urls", "x"], out var none));
        Assert.Null(none);
    }

    [Fact]
    public async Task SetsTheSecretFromTheReferenceAndNeverPrintsIt()
    {
        var (code, output, store, _) = await Run(Value, Args());
        Assert.Equal(0, code);
        Assert.Equal(Value, await store.GetAsync(Env, "WarehouseAPI", "apiKey", default));
        Assert.Contains("Set instance secret `apiKey` of WarehouseAPI", output);
        Assert.DoesNotContain(Value, output);
    }

    [Theory]
    [InlineData("--environment", "00000000-0000-0000-0000-0000000000ff", "--instance", "WarehouseAPI", "--name", "apiKey", "--from", "env:X")]
    [InlineData("--environment", Env, "--instance", "WarehouseAPI", "--name", "apiKey", "--from", "store:apiKey")]
    [InlineData("--environment", Env, "--instance", "WarehouseAPI", "--name", "apiKey")]
    [InlineData("--environment", Env, "--instance", "Warehouse_API", "--name", "apiKey", "--from", "env:X")]
    public async Task RefusesBadArgumentsAndSetsNothing(params string[] args)
    {
        var (code, output, store, _) = await Run(Value, args);
        Assert.NotEqual(0, code);
        Assert.DoesNotContain(Value, output);
        Assert.False(await store.ExistsAsync(Env, "WarehouseAPI", "apiKey", default));
    }

    [Fact]
    public async Task UnreadableReferenceFailsNamingIt()
    {
        var (code, output, store, _) = await Run(null, Args());
        Assert.Equal(1, code);
        Assert.Contains("Could not read kv:https://v.vault.azure.net/secrets/k", output);
        Assert.False(await store.ExistsAsync(Env, "WarehouseAPI", "apiKey", default));
    }

    [Theory]
    [InlineData("WarehouseAPI", "clientSecret")]
    [InlineData("StockAPI", "apiKey")]
    [InlineData("OrdersAPI", "apiKey")]
    public async Task RefusesASecretNoDesignLists(string instance, string name)
    {
        var (code, output, store, resolver) = await Run(Value, Args(instance, name));
        Assert.Equal(1, code);
        Assert.Contains($"No connection design in environment {Env} lists the instance secret `{name}` for {instance}", output);
        // Refused before the value is read.
        Assert.Equal(0, resolver.Reads);
        Assert.False(await store.ExistsAsync(Env, instance, name, default));
    }

    [Fact]
    public async Task RefusalNamesTheDesignsThatDidNotLoad()
    {
        var designs = new EnvironmentConfigTests.FakeDesigns();
        designs.Fail(Env, "ext_/brokerforge/WarehouseAPI/design.xml: not valid XML (unexpected end of file)");
        var (code, output, _, _) = await Run(Value, Args(), designs: designs);
        Assert.Equal(1, code);
        Assert.Contains("ext_/brokerforge/WarehouseAPI/design.xml: not valid XML", output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TableFailureIsReportedWithoutTheValue(bool onRead)
    {
        var (code, output, _, _) = await Run(Value, Args(), new RefusingStore(onRead));
        Assert.Equal(1, code);
        Assert.Contains("Could not store instance secret `apiKey` of WarehouseAPI in environment " + Env, output);
        Assert.Contains("HTTP 403 (AuthorizationPermissionMismatch)", output);
        Assert.DoesNotContain(Value, output);
        Assert.DoesNotContain("RequestId", output);
    }
}
