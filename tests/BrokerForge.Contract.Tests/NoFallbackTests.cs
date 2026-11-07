using System.Collections.Concurrent;
using Azure.Core;
using BrokerForge.Abstractions;
using BrokerForge.Auth;
using BrokerForge.Auth.Credentials;
using BrokerForge.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace BrokerForge.Contract.Tests;

/// <summary>
/// Phase 3: "no path falls back to the service account (test it)". Every test records each use of the
/// host's service identities (its TokenCredential and every secret resolved) and checks the source's own count
/// of data requests, so a fallback would show up even if it returned rows.
/// </summary>
[Collection(MockApiCollection.Name)]
public class NoFallbackTests(MockApiFixture mock) : IAsyncLifetime
{
    private sealed class RecordingCredential : TokenCredential
    {
        public int Calls;
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken ct) => GetTokenAsync(context, ct).Result;
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return ValueTask.FromResult(new AccessToken("service-identity-token", DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    private sealed class RecordingSecrets : ISecretResolver
    {
        private readonly EnvironmentSecretResolver _inner = new();
        public ConcurrentQueue<string> Resolved { get; } = new();
        public Task<string> GetAsync(SecretRef reference, CancellationToken ct)
        {
            Resolved.Enqueue(reference.ToString());
            return _inner.GetAsync(reference, ct);
        }
    }

    private readonly RecordingCredential _credential = new();
    private readonly RecordingSecrets _secrets = new();
    private readonly List<HostFactory> _hosts = new();

    public Task InitializeAsync() => mock.ResetAsync();

    public Task DisposeAsync()
    {
        foreach (var h in _hosts)
            h.Dispose();
        return Task.CompletedTask;
    }

    private HostFactory Host(params (string, string?)[] edits)
    {
        Environment.SetEnvironmentVariable("BF_TEST_TK_BOB", Tickets.BobPassword);
        var host = new HostFactory(mock.BaseUrl, extra: Tickets.HostSettings(mock.BaseUrl, edits), services: s =>
        {
            s.AddSingleton<TokenCredential>(_credential);
            s.AddSingleton<ISecretResolver>(_secrets);
        });
        host.CreateClient().Dispose();
        _hosts.Add(host);
        return host;
    }

    private static readonly (string, string?)[] ServiceIdentityRule =
    {
        ("Instances:TicketsAPI:Auth:AsyncContext", "ServiceIdentity"),
        ("Instances:TicketsAPI:Auth:AsyncAuth:Mode", "Static"),
        ("Instances:TicketsAPI:Auth:AsyncAuth:StaticKind", "basic"),
        ("Instances:TicketsAPI:Auth:AsyncAuth:Username", "bob"),
        ("Instances:TicketsAPI:Auth:AsyncAuth:SecretRef", "env:BF_TEST_TK_BOB"),
    };

    private static CanonicalRequest ListAs(string? user, bool async = false)
    {
        var request = HostFactory.Request("ext_ticket", Messages.RetrieveMultiple, "TicketsAPI");
        request.Query = new CanonicalQuery { Page = new PageRequest { Size = 50 } };
        request.Caller = new CallerContext { UserObjectId = user, Async = async };
        return request;
    }

    private static string[] Titles(CanonicalResponse response) =>
        response.Rows.Select(r => r.Cells.Single(c => c.Col == "ext_title").Value!).ToArray();

    private static string[] TitlesOf(string[] ids) => ids.Select(id => "Ticket " + id[3..]).ToArray();

    /// <summary>Runs the request and returns the reply plus what the service identities and the source saw during it.</summary>
    private async Task<(int Status, CanonicalResponse? Response, ErrorPayload? Error, int TokenCalls, string[] Secrets, int SourceRequests)> Run(HostFactory host, CanonicalRequest request)
    {
        var calls = _credential.Calls;
        var secrets = _secrets.Resolved.Count;
        var before = await Tickets.DataRequestsAsync(mock.BaseUrl);
        var (status, response, error) = await host.Execute(request);
        return (status, response, error, _credential.Calls - calls, _secrets.Resolved.Skip(secrets).ToArray(), await Tickets.DataRequestsAsync(mock.BaseUrl) - before);
    }

    private static void AssertConnectionRequired(int status, ErrorPayload? error)
    {
        Assert.Equal(401, status);
        Assert.Equal(nameof(ConnectionRequiredException), error!.Error);
        Assert.Equal(Tickets.ConnectBase + "/connect/TicketsAPI", error.ConnectUrl);
    }

    [Fact]
    public async Task PerUser_NoCredential_SyncCall_ConnectionRequired_NoServiceCredentialUsed()
    {
        var r = await Run(Host(), ListAs(Tickets.AliceOid));
        AssertConnectionRequired(r.Status, r.Error);
        Assert.Contains("no credential", r.Error!.Message);
        Assert.Equal((0, 0), (r.TokenCalls, r.SourceRequests));
        Assert.Empty(r.Secrets);
    }

    [Fact]
    public async Task PerUser_NoCredential_AsyncTriggeringUser_ConnectionRequired()
    {
        var r = await Run(Host(), ListAs(Tickets.AliceOid, async: true));
        AssertConnectionRequired(r.Status, r.Error);
        Assert.Equal((0, 0), (r.TokenCalls, r.SourceRequests));
        Assert.Empty(r.Secrets);
    }

    [Fact]
    public async Task PerUser_NoCallerObjectId_ConnectionRequired()
    {
        var r = await Run(Host(), ListAs(null));
        AssertConnectionRequired(r.Status, r.Error);
        Assert.Contains("Entra object ID", r.Error!.Message);
        Assert.Equal((0, 0), (r.TokenCalls, r.SourceRequests));
    }

    [Fact]
    public async Task AsyncRule_ServiceIdentity_SyncCallStillPerUser()
    {
        var r = await Run(Host(ServiceIdentityRule), ListAs(Tickets.AliceOid));
        AssertConnectionRequired(r.Status, r.Error);
        Assert.Equal((0, 0), (r.TokenCalls, r.SourceRequests));
        Assert.Empty(r.Secrets);
    }

    [Fact]
    public async Task AsyncRule_ServiceIdentity_AsyncCallUsesAsyncAuth()
    {
        var r = await Run(Host(ServiceIdentityRule), ListAs(Tickets.AliceOid, async: true));
        Assert.True(r.Status == 200, r.Error?.Message);
        Assert.Equal(TitlesOf(Tickets.BobTickets), Titles(r.Response!));
        Assert.Equal(new[] { "env:BF_TEST_TK_BOB" }, r.Secrets);
    }

    [Fact]
    public async Task PerUser_ExpiredToken_RefreshRejected_NoRetryAsAnotherMode()
    {
        var host = Host();
        var store = host.Services.GetRequiredService<IUserCredentialStore>();
        await store.SaveAsync("TicketsAPI", Tickets.AliceOid, Tickets.OAuthCredential(("tk-at-old", "tk-rt-unknown", 0), DateTimeOffset.UtcNow.AddMinutes(-1)), default);
        var r = await Run(host, ListAs(Tickets.AliceOid));
        AssertConnectionRequired(r.Status, r.Error);
        Assert.Contains("refused", r.Error!.Message);
        Assert.Equal((0, 0), (r.TokenCalls, r.SourceRequests));
        Assert.Equal(new[] { "env:BF_TEST_TK_CLIENT_SECRET" }, r.Secrets);
        Assert.Equal(CredentialStatus.Expired, (await store.GetAsync("TicketsAPI", Tickets.AliceOid, default))!.Status);
    }

    [Fact]
    public async Task PerUser_SourceReturns401_SourceExceptionNotRetried()
    {
        var host = Host();
        await host.Services.GetRequiredService<IUserCredentialStore>()
            .SaveAsync("TicketsAPI", Tickets.AliceOid, Tickets.OAuthCredential(("tk-at-revoked", "tk-rt-x", 3600)), default);
        var r = await Run(host, ListAs(Tickets.AliceOid));
        Assert.Equal(502, r.Status);
        Assert.Equal(nameof(SourceException), r.Error!.Error);
        Assert.Contains("401", r.Error.Message);
        Assert.Equal((0, 1), (r.TokenCalls, r.SourceRequests));
        Assert.Empty(r.Secrets);
    }

    [Fact]
    public async Task TwoUsers_SameQuery_DifferentRows()
    {
        var host = Host();
        var store = host.Services.GetRequiredService<IUserCredentialStore>();
        await store.SaveAsync("TicketsAPI", Tickets.AliceOid, Tickets.OAuthCredential(await Tickets.SignInAsync(mock.BaseUrl, "alice")), default);
        await store.SaveAsync("TicketsAPI", Tickets.BobOid, Tickets.OAuthCredential(await Tickets.SignInAsync(mock.BaseUrl, "bob")), default);
        var alice = await Run(host, ListAs(Tickets.AliceOid));
        var bob = await Run(host, ListAs(Tickets.BobOid));
        Assert.True(alice.Status == 200, alice.Error?.Message);
        Assert.True(bob.Status == 200, bob.Error?.Message);
        Assert.Equal(TitlesOf(Tickets.AliceTickets), Titles(alice.Response!));
        Assert.Equal(TitlesOf(Tickets.BobTickets), Titles(bob.Response!));
        Assert.Equal((0, 0), (alice.TokenCalls, bob.TokenCalls));
    }
}
