using System.Net.Sockets;
using Azure.Data.Tables;
using BrokerForge.Auth.Credentials;
using Microsoft.AspNetCore.DataProtection;

namespace BrokerForge.Auth.Tests;

/// <summary>The same contract for every <see cref="IUserCredentialStore"/> (HLD D-25).</summary>
public abstract class CredentialStoreContract
{
    protected const string Alice = "a11ce000-0000-0000-0000-000000000001";
    protected const string Bob = "b0b00000-0000-0000-0000-000000000002";
    protected const string AccessToken = "mock-at-ZXhhbXBsZQ";
    protected const string RefreshToken = "mock-rt-c2VjcmV0";

    protected abstract IUserCredentialStore Store { get; }

    protected static UserCredential OAuth(string access = AccessToken) =>
        new(CredentialKind.OAuth, CredentialStatus.Connected, access, RefreshToken, DateTimeOffset.Parse("2026-11-01T20:00:00Z"), null, null, DateTimeOffset.Parse("2026-11-01T19:00:00Z"));

    [Fact]
    public async Task Store_RoundTrip_ReturnsSavedCredential()
    {
        await Store.SaveAsync("Tickets", Alice, OAuth(), default);
        Assert.Equal(OAuth(), await Store.GetAsync("Tickets", Alice, default));
    }

    [Fact]
    public async Task Store_IsolatesUsersAndInstances()
    {
        await Store.SaveAsync("Tickets", Alice, OAuth("alice-tickets"), default);
        await Store.SaveAsync("Tickets", Bob, OAuth("bob-tickets"), default);
        await Store.SaveAsync("Other", Alice, OAuth("alice-other"), default);
        Assert.Equal("alice-tickets", (await Store.GetAsync("Tickets", Alice, default))!.AccessToken);
        Assert.Equal("bob-tickets", (await Store.GetAsync("Tickets", Bob, default))!.AccessToken);
        Assert.Equal("alice-other", (await Store.GetAsync("Other", Alice, default))!.AccessToken);
        await Store.DeleteAsync("Tickets", Alice, default);
        Assert.Null(await Store.GetAsync("Tickets", Alice, default));
        Assert.NotNull(await Store.GetAsync("Other", Alice, default));
    }

    [Fact]
    public async Task Store_GetVersioned_ReturnsCredentialAndVersion()
    {
        await Store.SaveAsync("Tickets", Alice, OAuth(), default);
        var row = await Store.GetVersionedAsync("Tickets", Alice, default);
        Assert.Equal(OAuth(), row!.Credential);
        Assert.False(string.IsNullOrEmpty(row.Version));
        Assert.Null(await Store.GetVersionedAsync("Tickets", Bob, default));
    }

    [Fact]
    public async Task Store_SetStatusIfUnchanged_KeepsPayload()
    {
        await Store.SaveAsync("Tickets", Alice, OAuth(), default);
        var row = await Store.GetVersionedAsync("Tickets", Alice, default);
        Assert.True(await Store.SetStatusIfUnchangedAsync("Tickets", Alice, CredentialStatus.Expired, row!.Version, default));
        Assert.Equal(OAuth() with { Status = CredentialStatus.Expired }, await Store.GetAsync("Tickets", Alice, default));
    }

    [Fact]
    public async Task Store_ConditionalWrites_StaleVersion_ReturnFalseAndChangeNothing()
    {
        await Store.SaveAsync("Tickets", Alice, OAuth("first"), default);
        var stale = (await Store.GetVersionedAsync("Tickets", Alice, default))!.Version;
        await Store.SaveAsync("Tickets", Alice, OAuth("reconnected"), default);
        Assert.False(await Store.SetStatusIfUnchangedAsync("Tickets", Alice, CredentialStatus.Expired, stale, default));
        Assert.False(await Store.ReplaceIfUnchangedAsync("Tickets", Alice, OAuth("refreshed"), stale, default));
        Assert.Equal(OAuth("reconnected"), await Store.GetAsync("Tickets", Alice, default));
    }

    [Fact]
    public async Task Store_ReplaceIfUnchanged_CurrentVersion_Replaces()
    {
        await Store.SaveAsync("Tickets", Alice, OAuth("first"), default);
        var row = await Store.GetVersionedAsync("Tickets", Alice, default);
        Assert.True(await Store.ReplaceIfUnchangedAsync("Tickets", Alice, OAuth("refreshed"), row!.Version, default));
        var after = await Store.GetVersionedAsync("Tickets", Alice, default);
        Assert.Equal(OAuth("refreshed"), after!.Credential);
        Assert.NotEqual(row.Version, after.Version);
    }

    [Fact]
    public async Task Store_ConditionalWrites_RowDeleted_ReturnFalseAndDoNotRecreate()
    {
        await Store.SaveAsync("Tickets", Alice, OAuth(), default);
        var version = (await Store.GetVersionedAsync("Tickets", Alice, default))!.Version;
        await Store.DeleteAsync("Tickets", Alice, default);
        Assert.False(await Store.ReplaceIfUnchangedAsync("Tickets", Alice, OAuth("refreshed"), version, default));
        Assert.False(await Store.SetStatusIfUnchangedAsync("Tickets", Alice, CredentialStatus.Expired, version, default));
        Assert.Null(await Store.GetAsync("Tickets", Alice, default));
    }

    [Theory]
    [InlineData("Tickets", "not-a-guid")]
    [InlineData("Tick/ets", Alice)]
    [InlineData("", Alice)]
    public async Task Store_InvalidKeys_Refused(string instance, string user)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Store.GetAsync(instance, user, default));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.SaveAsync(instance, user, OAuth(), default));
    }
}

public sealed class InMemoryCredentialStoreTests : CredentialStoreContract
{
    protected override IUserCredentialStore Store { get; } = new InMemoryUserCredentialStore();

    [Fact]
    public void UserCredential_ToString_HasNoSecrets()
    {
        var text = (OAuth() with { Username = "alice", Secret = "sso-pass-123" }).ToString();
        Assert.DoesNotContain(AccessToken, text);
        Assert.DoesNotContain(RefreshToken, text);
        Assert.DoesNotContain("sso-pass-123", text);
        Assert.Contains("OAuth", text);
        Assert.Contains("Connected", text);
    }
}

[Collection(AzuriteCollection.Name)]
public sealed class TableCredentialStoreTests : CredentialStoreContract
{
    private readonly TableClient _table;

    public TableCredentialStoreTests(AzuriteFixture _)
    {
        _table = new TableServiceClient(AzuriteFixture.ConnectionString).GetTableClient("bfcreds" + Guid.NewGuid().ToString("N")[..12]);
        _table.CreateIfNotExists();
        Store = new TableUserCredentialStore(_table, new EphemeralDataProtectionProvider());
    }

    protected override IUserCredentialStore Store { get; }

    [Fact]
    public async Task Table_PayloadIsCiphertext()
    {
        await Store.SaveAsync("Tickets", Alice, OAuth() with { Secret = "sso-pass-123" }, default);
        var entity = (await _table.GetEntityAsync<TableEntity>("Tickets", Alice)).Value;
        var raw = string.Join("|", ((IDictionary<string, object>)entity).Values.Select(v => v?.ToString()));
        Assert.DoesNotContain(AccessToken, raw);
        Assert.DoesNotContain(RefreshToken, raw);
        Assert.DoesNotContain("sso-pass-123", raw);
        Assert.Equal("Connected", entity.GetString("Status"));
        Assert.Equal("OAuth", entity.GetString("Kind"));
    }

    [Fact]
    public async Task Table_PayloadFromAnotherKeyRing_UnreadableNotCrypto()
    {
        await Store.SaveAsync("Tickets", Alice, OAuth(), default);
        var other = new TableUserCredentialStore(_table, new EphemeralDataProtectionProvider());
        var ex = await Assert.ThrowsAsync<CredentialUnreadableException>(() => other.GetAsync("Tickets", Alice, default));
        Assert.Contains("Tickets", ex.Message);
        Assert.DoesNotContain(AccessToken, ex.ToString());
        await other.DeleteAsync("Tickets", Alice, default);
        Assert.Null(await other.GetAsync("Tickets", Alice, default));
    }
}
