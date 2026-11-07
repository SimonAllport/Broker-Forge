using BrokerForge.Auth.Credentials;
using BrokerForge.Contracts;
using BrokerForge.Keys;
using Microsoft.Extensions.DependencyInjection;

namespace BrokerForge.Contract.Tests;

/// <summary>
/// The phase 3 done criterion in each per-user mode: the same TicketsAPI descriptors return each user their own
/// rows (Phase 3). OAuth tokens come from the mock's real sign-in, SSO from the users' passwords and
/// Impersonate from Entra-style tokens signed with the test key.
/// </summary>
[Collection(MockApiCollection.Name)]
public class PerUserEndToEndTests(MockApiFixture mock) : IAsyncLifetime
{
    private static readonly ReversibleKeyStrategy Keys = new(KeyKind.String);
    private readonly List<HostFactory> _hosts = new();

    public Task InitializeAsync() => mock.ResetAsync();

    public Task DisposeAsync()
    {
        foreach (var h in _hosts)
            h.Dispose();
        return Task.CompletedTask;
    }

    public enum Mode { OAuth, Sso, Impersonate }

    private static (string, string?)[] Settings(Mode mode) => mode switch
    {
        Mode.Sso => new (string, string?)[]
        {
            ("Instances:TicketsAPI:Auth:Mode", "Sso"), ("Instances:TicketsAPI:Auth:SsoKind", "basic"), ("Instances:TicketsAPI:Auth:OAuthResource", null),
        },
        Mode.Impersonate => new (string, string?)[]
        {
            ("Instances:TicketsAPI:Auth:Mode", "Impersonate"), ("Instances:TicketsAPI:Auth:OAuthResource", "Entra"),
            ("OAuthResources:Entra:AuthorizeUrl", "https://login.test/authorize"), ("OAuthResources:Entra:TokenUrl", "https://login.test/token"),
            ("OAuthResources:Entra:Scopes:0", Tickets.EntraAudience + "/user_impersonation"), ("OAuthResources:Entra:Scopes:1", "offline_access"),
            ("OAuthResources:Entra:UsesConnectApp", "true"),
        },
        _ => Array.Empty<(string, string?)>(),
    };

    /// <summary>A host in the given mode with alice and bob both connected.</summary>
    private async Task<HostFactory> Connected(Mode mode)
    {
        var host = new HostFactory(mock.BaseUrl, extra: Tickets.HostSettings(mock.BaseUrl, Settings(mode)));
        _hosts.Add(host);
        var store = host.Services.GetRequiredService<IUserCredentialStore>();
        foreach (var (user, oid) in new[] { ("alice", Tickets.AliceOid), ("bob", Tickets.BobOid) })
        {
            var credential = mode switch
            {
                Mode.OAuth => Tickets.OAuthCredential(await Tickets.SignInAsync(mock.BaseUrl, user)),
                Mode.Sso => new UserCredential(CredentialKind.Sso, CredentialStatus.Connected, null, null, null, user, Tickets.Password(user), DateTimeOffset.UtcNow),
                _ => new UserCredential(CredentialKind.Impersonate, CredentialStatus.Connected, Tickets.EntraToken(oid), "entra-rt-unused",
                    DateTimeOffset.UtcNow.AddHours(1), null, null, DateTimeOffset.UtcNow),
            };
            await store.SaveAsync("TicketsAPI", oid, credential, default);
        }
        return host;
    }

    private static CanonicalRequest As(string oid, string message)
    {
        var request = HostFactory.Request("ext_ticket", message, "TicketsAPI");
        request.Caller = new CallerContext { UserObjectId = oid };
        return request;
    }

    private static async Task<string[]> Titles(HostFactory host, string oid)
    {
        var request = As(oid, Messages.RetrieveMultiple);
        request.Query = new CanonicalQuery { Page = new PageRequest { Size = 50 } };
        var (status, response, error) = await host.Execute(request);
        Assert.True(status == 200, error?.Message);
        return response!.Rows.Select(r => r.Cells.Single(c => c.Col == "ext_title").Value!).ToArray();
    }

    private static string[] TitlesOf(string[] ids) => ids.Select(id => "Ticket " + id[3..]).ToArray();

    [Theory]
    [InlineData(Mode.OAuth)]
    [InlineData(Mode.Sso)]
    [InlineData(Mode.Impersonate)]
    public async Task TwoUsers_DifferentRows(Mode mode)
    {
        var host = await Connected(mode);
        Assert.Equal(TitlesOf(Tickets.AliceTickets), await Titles(host, Tickets.AliceOid));
        Assert.Equal(TitlesOf(Tickets.BobTickets), await Titles(host, Tickets.BobOid));
    }

    [Theory]
    [InlineData(Mode.OAuth)]
    [InlineData(Mode.Sso)]
    [InlineData(Mode.Impersonate)]
    public async Task PerUser_Create_OwnedByCaller(Mode mode)
    {
        var host = await Connected(mode);
        var create = As(Tickets.BobOid, Messages.Create);
        create.Record = new() { new Cell { Col = "ext_title", Type = CellTypes.String, Value = "Printer jam" }, new Cell { Col = "ext_priority", Type = CellTypes.OptionSet, Value = "100000002" } };
        var (status, response, error) = await host.Execute(create);
        Assert.True(status == 200, error?.Message);
        Assert.Equal(Keys.Encode(ExternalKey.FromString("TK-13")).ToString(), response!.Id);
        Assert.Contains("Printer jam", await Titles(host, Tickets.BobOid));
        Assert.DoesNotContain("Printer jam", await Titles(host, Tickets.AliceOid));
    }

    [Theory]
    [InlineData(Mode.OAuth)]
    [InlineData(Mode.Sso)]
    [InlineData(Mode.Impersonate)]
    public async Task PerUser_RetrieveOtherUsersTicket_NotFound(Mode mode)
    {
        var host = await Connected(mode);
        var own = As(Tickets.BobOid, Messages.Retrieve);
        own.Id = Keys.Encode(ExternalKey.FromString("TK-8")).ToString();
        var (ownStatus, ownResponse, _) = await host.Execute(own);
        Assert.Equal(200, ownStatus);
        Assert.Equal("Ticket 8", ownResponse!.Record.Cells.Single(c => c.Col == "ext_title").Value);

        var other = As(Tickets.AliceOid, Messages.Retrieve);
        other.Id = own.Id;
        var (status, _, error) = await host.Execute(other);
        Assert.Equal(404, status);
        Assert.Equal(SourceErrorKinds.NotFound, error!.Error);
    }
}
