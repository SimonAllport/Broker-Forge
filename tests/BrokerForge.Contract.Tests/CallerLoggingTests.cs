namespace BrokerForge.Contract.Tests;

/// <summary>Per-user modes key credentials by the caller, so every execute log line says who called and whether async (HLD D-27, D-29).</summary>
[Collection(MockApiCollection.Name)]
public class CallerLoggingTests(MockApiFixture mock) : HostTestBase(mock)
{
    [Fact]
    public async Task Execute_LogScopeCarriesCallerAndAsync()
    {
        var request = List("ext_order", 5);
        request.Caller = new() { UserObjectId = "8f1c2a00-0000-0000-0000-0000000000aa", Async = true };
        var (status, _, _) = await Host.Execute(request);
        Assert.Equal(200, status);
        var line = Host.Logs.First(l => l.StartsWith("Executing RetrieveMultiple"));
        Assert.Contains("userObjectId=8f1c2a00-0000-0000-0000-0000000000aa", line);
        Assert.Contains("async=True", line);
    }

    [Fact]
    public async Task Execute_NoCallerObjectId_LoggedAsNone()
    {
        var request = List("ext_order", 5);
        request.Caller = new() { UserObjectId = null, Async = false };
        await Host.Execute(request);
        var line = Host.Logs.First(l => l.StartsWith("Executing RetrieveMultiple"));
        Assert.Contains("userObjectId=(none)", line);
        Assert.Contains("async=False", line);
    }
}
