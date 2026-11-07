using BrokerForge.Abstractions;
using BrokerForge.Auth.Credentials;
using BrokerForge.Connections;
using BrokerForge.Contracts;
using BrokerForge.Host.Config;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BrokerForge.Contract.Tests.Config;

/// <summary>The host as deployed since plan 5: every source is a connection design, and the image carries no descriptor files.</summary>
[Collection(MockApiCollection.Name)]
public sealed class NoFileConfigTests(MockApiFixture mock) : IDisposable
{
    private readonly EnvironmentConfigTests.FakeDesigns _designs = new();
    private readonly InMemoryInstanceSecretStore _secrets = new();
    private HostFactory? _host;

    private HostFactory Host => _host ??= new HostFactory(mock.BaseUrl, files: false, extra: new Dictionary<string, string?>
    {
        ["Credentials:InMemory"] = "true",
        ["Config:Designs:AllowedPrivateHosts:0"] = "127.0.0.1",
        ["Config:Designs:AllowLoopbackForTests"] = "true",
    }, services: s =>
    {
        s.AddSingleton<IDesignSource>(_designs);
        s.AddSingleton<IInstanceSecretStore>(_secrets);
    });

    private static CanonicalRequest List(string table, string? instance = null)
    {
        var request = HostFactory.Request(table, Messages.RetrieveMultiple, instance);
        request.Query = new CanonicalQuery { Page = new PageRequest { Size = 5 } };
        return request;
    }

    [Fact]
    public void FromConfiguration_NoPaths_EmptyStoreNoProblems()
    {
        var store = HostConfigStore.FromConfiguration(new ConfigurationBuilder().Build());
        Assert.Empty(store.Instances);
    }

    [Fact]
    public async Task Host_NoFiles_StartsAndServesADesign()
    {
        _designs.Add(HostFactory.Environment1, EnvironmentConfigTests.Warehouse(), new EnvironmentSettings(mock.BaseUrl, new Dictionary<string, string>()));
        await _secrets.SetAsync(HostFactory.Environment1, "WarehouseAPI", "apiKey", MockApiFixture.InventoryKey, default);
        var (status, response, error) = await Host.Execute(List("ext_item", "WarehouseAPI"));
        Assert.True(status == 200, error?.Message);
        Assert.NotEmpty(response!.Rows);
    }

    [Fact]
    public async Task Host_NoFiles_UnknownTable_TypedError()
    {
        var (status, _, error) = await Host.Execute(List("ext_order"));
        Assert.Equal(400, status);
        Assert.Equal(nameof(UnsupportedQueryException), error!.Error);
        Assert.Equal("Table ext_order is not mapped to a BrokerForge source.", error.Message);
    }

    public void Dispose() => _host?.Dispose();
}
