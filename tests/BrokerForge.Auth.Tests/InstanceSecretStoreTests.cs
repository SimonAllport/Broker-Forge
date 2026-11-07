using Azure.Data.Tables;
using BrokerForge.Auth.Credentials;
using Microsoft.AspNetCore.DataProtection;

namespace BrokerForge.Auth.Tests;

/// <summary>The same contract for every <see cref="IInstanceSecretStore"/> (spec 8, HLD D-34).</summary>
public abstract class InstanceSecretStoreContract
{
    protected const string EnvA = "00000000-0000-0000-0000-00000000000a";
    protected const string EnvB = "00000000-0000-0000-0000-00000000000b";
    protected const string Value = "bf-test-instance-secret-value";

    protected abstract IInstanceSecretStore Store { get; }

    [Fact]
    public async Task InstanceSecretRoundTrip()
    {
        Assert.Null(await Store.GetAsync(EnvA, "WarehouseAPI", "apiKey", default));
        Assert.False(await Store.ExistsAsync(EnvA, "WarehouseAPI", "apiKey", default));
        await Store.SetAsync(EnvA, "WarehouseAPI", "apiKey", Value, default);
        Assert.Equal(Value, await Store.GetAsync(EnvA, "WarehouseAPI", "apiKey", default));
        Assert.True(await Store.ExistsAsync(EnvA, "WarehouseAPI", "apiKey", default));
        await Store.SetAsync(EnvA, "WarehouseAPI", "apiKey", Value + "-2", default);
        Assert.Equal(Value + "-2", await Store.GetAsync(EnvA, "WarehouseAPI", "apiKey", default));
        await Store.DeleteAsync(EnvA, "WarehouseAPI", "apiKey", default);
        Assert.Null(await Store.GetAsync(EnvA, "WarehouseAPI", "apiKey", default));
        Assert.False(await Store.ExistsAsync(EnvA, "WarehouseAPI", "apiKey", default));
    }

    [Fact]
    public async Task InstanceSecretsAreKeyedByEnvironment()
    {
        await Store.SetAsync(EnvA, "WarehouseAPI", "apiKey", "in-a", default);
        Assert.Null(await Store.GetAsync(EnvB, "WarehouseAPI", "apiKey", default));
        await Store.SetAsync(EnvB, "WarehouseAPI", "apiKey", "in-b", default);
        Assert.Equal("in-a", await Store.GetAsync(EnvA.ToUpperInvariant(), "WarehouseAPI", "apiKey", default));
        Assert.Equal("in-b", await Store.GetAsync(EnvB, "WarehouseAPI", "apiKey", default));
        await Store.DeleteAsync(EnvA, "WarehouseAPI", "apiKey", default);
        Assert.Equal("in-b", await Store.GetAsync(EnvB, "WarehouseAPI", "apiKey", default));
    }

    [Fact]
    public async Task InstanceSecretsAreKeyedByInstanceAndName()
    {
        await Store.SetAsync(EnvA, "Warehouse", "apiKey", "one", default);
        await Store.SetAsync(EnvA, "Warehouse", "clientSecret", "two", default);
        await Store.SetAsync(EnvA, "Stock", "apiKey", "three", default);
        Assert.Equal("one", await Store.GetAsync(EnvA, "Warehouse", "apiKey", default));
        Assert.Equal("two", await Store.GetAsync(EnvA, "Warehouse", "clientSecret", default));
        Assert.Equal("three", await Store.GetAsync(EnvA, "Stock", "apiKey", default));
    }

    [Fact]
    public async Task DeletingAMissingSecretIsNotAnError() =>
        await Store.DeleteAsync(EnvA, "WarehouseAPI", "nothing", default);

    [Theory]
    [InlineData("not-a-guid", "WarehouseAPI", "apiKey")]
    [InlineData(EnvA, "Ware/house", "apiKey")]
    [InlineData(EnvA, "", "apiKey")]
    [InlineData(EnvA, "WarehouseAPI", "api_key")]
    [InlineData(EnvA, "WarehouseAPI", "")]
    public async Task InvalidKeys_Refused(string env, string instance, string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Store.GetAsync(env, instance, name, default));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.SetAsync(env, instance, name, Value, default));
    }
}

public sealed class InMemoryInstanceSecretStoreTests : InstanceSecretStoreContract
{
    protected override IInstanceSecretStore Store { get; } = new InMemoryInstanceSecretStore();
}

[Collection(AzuriteCollection.Name)]
public sealed class TableInstanceSecretStoreTests : InstanceSecretStoreContract
{
    private readonly TableClient _table;

    public TableInstanceSecretStoreTests(AzuriteFixture _)
    {
        _table = new TableServiceClient(AzuriteFixture.ConnectionString).GetTableClient("bfsecrets" + Guid.NewGuid().ToString("N")[..12]);
        _table.CreateIfNotExists();
        Store = new TableInstanceSecretStore(_table, new EphemeralDataProtectionProvider());
    }

    protected override IInstanceSecretStore Store { get; }

    [Fact]
    public async Task StoredRowHasNoPlainValue()
    {
        await Store.SetAsync(EnvA, "WarehouseAPI", "apiKey", Value, default);
        var entity = (await _table.GetEntityAsync<TableEntity>($"instancesecret_{Guid.Parse(EnvA):N}", "WarehouseAPI_apiKey")).Value;
        var raw = string.Join("|", ((IDictionary<string, object>)entity).Values.Select(v => v?.ToString()));
        Assert.DoesNotContain(Value, raw);
        Assert.DoesNotContain(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Value)), raw);
    }

    [Fact]
    public async Task ValueFromAnotherKeyRing_UnreadableNotCrypto()
    {
        await Store.SetAsync(EnvA, "WarehouseAPI", "apiKey", Value, default);
        var other = new TableInstanceSecretStore(_table, new EphemeralDataProtectionProvider());
        var ex = await Assert.ThrowsAsync<CredentialUnreadableException>(() => other.GetAsync(EnvA, "WarehouseAPI", "apiKey", default));
        Assert.Contains("WarehouseAPI", ex.Message);
        Assert.DoesNotContain(Value, ex.ToString());
        Assert.True(await other.ExistsAsync(EnvA, "WarehouseAPI", "apiKey", default));
    }
}
