using BrokerForge.Contracts;

namespace BrokerForge.Contract.Tests;

[Collection(MockApiCollection.Name)]
public class IngressTests(MockApiFixture mock) : IAsyncLifetime
{
    private HostFactory _host = null!;

    public async Task InitializeAsync()
    {
        await mock.ResetAsync();
        _host = new HostFactory(mock.BaseUrl);
    }

    public Task DisposeAsync()
    {
        _host.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task NoToken_401()
    {
        var (status, _, _) = await _host.Execute(ListRequest(), token: null);
        Assert.Equal(401, status);
    }

    [Fact]
    public async Task WrongAudience_401()
    {
        var (status, _, _) = await _host.Execute(ListRequest(), HostFactory.Token(aud: "api://someone-else"));
        Assert.Equal(401, status);
    }

    [Fact]
    public async Task WrongTenant_401()
    {
        var (status, _, _) = await _host.Execute(ListRequest(), HostFactory.Token(tid: "00000000-0000-0000-0000-000000000009"));
        Assert.Equal(401, status);
    }

    [Fact]
    public async Task UnknownAzp_403()
    {
        var (status, _, error) = await _host.Execute(ListRequest(), HostFactory.Token(azp: "00000000-0000-0000-0000-00000000dead"));
        Assert.Equal(403, status);
        Assert.Equal("Forbidden", error!.Error);
    }

    [Fact]
    public async Task EnvironmentMismatch_403()
    {
        var request = ListRequest();
        request.EnvironmentId = HostFactory.Environment2;
        var (status, _, error) = await _host.Execute(request);
        Assert.Equal(403, status);
        Assert.Equal(request.CorrelationId, error!.CorrelationId);
        // The reply names what was asked for, never the environment this client is allowed to reach.
        Assert.Contains(HostFactory.Environment2, error.Message);
        Assert.DoesNotContain(HostFactory.Environment1, error.Message, StringComparison.OrdinalIgnoreCase);
        // The other caller is allowed for environment 2 only.
        Assert.Equal(200, (await _host.Execute(request, HostFactory.Token(azp: HostFactory.OtherApp))).Status);
    }

    [Fact]
    public async Task MatchingCallerAndEnvironment_200()
    {
        var request = ListRequest();
        var (status, response, _) = await _host.Execute(request);
        Assert.Equal(200, status);
        Assert.Equal(request.CorrelationId, response!.CorrelationId);
        Assert.Equal(10, response.Rows.Count);
    }

    [Fact]
    public async Task MissingCorrelationId_400()
    {
        var request = ListRequest();
        request.CorrelationId = null;
        Assert.Equal(400, (await _host.Execute(request)).Status);
    }

    internal static CanonicalRequest ListRequest(int size = 10)
    {
        var request = HostFactory.Request("ext_order", Messages.RetrieveMultiple);
        request.Query = new CanonicalQuery { Page = new PageRequest { Size = size } };
        return request;
    }
}
