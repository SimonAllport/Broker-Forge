using System.Diagnostics;
using BrokerForge.Abstractions;
using BrokerForge.Adapters.Rest;
using BrokerForge.Auth;
using BrokerForge.Contracts;
using BrokerForge.Descriptors.Model;
using BrokerForge.Descriptors.Parsing;
using BrokerForge.Host.Execution;

namespace BrokerForge.Contract.Tests;

[Collection(MockApiCollection.Name)]
public class ExecutorTests(MockApiFixture mock) : IAsyncLifetime
{
    public Task InitializeAsync() => mock.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class Key : ISecretResolver
    {
        public Task<string> GetAsync(SecretRef reference, CancellationToken ct) => Task.FromResult(MockApiFixture.ApiKey);
    }

    private ServiceInstance Instance(string? baseUrl = null) => new("OrdersAPI", "rest", baseUrl ?? mock.BaseUrl,
        new AuthConfig(AuthMode.Static, "apiKeyHeader", HeaderName: "X-Api-Key", SecretRef: "env:UNUSED"),
        new Dictionary<string, string>(), new Dictionary<string, string>());

    private async Task<IAuthenticatedChannel> Channel(ServiceInstance instance) =>
        await new StaticAuthProvider(new Key(), new HttpClient()).AuthenticateAsync(instance, new CallerContext(), "corr-1", CancellationToken.None);

    private static OperationDescriptor Descriptor(string retry) => DescriptorParser.Parse(
        "operation: probe\nversion: 1\nbinds: Delete\nrequest:\n  method: GET\n  path: /v2/orders\n" + retry, "probe.yaml");

    private static RenderedRequest Get(string baseUrl, string pathAndQuery) =>
        new(baseUrl, "GET", pathAndQuery, Array.Empty<KeyValuePair<string, string>>(), null, null, new HashSet<string>(), Array.Empty<string>());

    private static readonly OperationExecutor Executor = new(new RestBroker());

    [Fact]
    public async Task Execute_ReturnsStatusHeadersAndBody()
    {
        var instance = Instance();
        var result = await Executor.ExecuteAsync(Descriptor(""), Get(instance.BaseUrl, "/v2/orders?limit=2"), await Channel(instance), Deadline.After(TimeSpan.FromSeconds(10)), "corr-1", CancellationToken.None);
        Assert.Equal(200, result.Status);
        Assert.StartsWith("application/json", result.Header("content-type"));
        Assert.Contains("ord_0001", System.Text.Encoding.UTF8.GetString(result.Body));
    }

    [Fact]
    public async Task Execute_SlowSource_SourceTimeoutWithinBudget()
    {
        var instance = Instance();
        var watch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<SourceTimeoutException>(() => Executor.ExecuteAsync(
            Descriptor(""), Get(instance.BaseUrl, "/v2/orders?customer=__slow"), Channel(instance).Result, Deadline.After(TimeSpan.FromSeconds(2)), "corr-1", CancellationToken.None));
        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(1.9), TimeSpan.FromSeconds(3));
        Assert.Equal("corr-1", ex.CorrelationId);
    }

    [Fact]
    public async Task Retry_503Once_SucceedsWhenIdempotent()
    {
        var instance = Instance();
        var d = Descriptor("retry:\n  idempotent: true\n  attempts: 3\n  on: [503]\n");
        var result = await Executor.ExecuteAsync(d, Get(instance.BaseUrl, "/v2/orders?customer=__fail503once"), await Channel(instance), Deadline.After(TimeSpan.FromSeconds(10)), "c", CancellationToken.None);
        Assert.Equal(200, result.Status);
    }

    [Fact]
    public async Task Retry_NotIdempotent_NoRetry()
    {
        var instance = Instance();
        var result = await Executor.ExecuteAsync(Descriptor(""), Get(instance.BaseUrl, "/v2/orders?customer=__fail503once"), await Channel(instance), Deadline.After(TimeSpan.FromSeconds(10)), "c", CancellationToken.None);
        Assert.Equal(503, result.Status);
    }

    [Fact]
    public async Task Retry_OnlyListedStatuses()
    {
        var instance = Instance();
        var d = Descriptor("retry:\n  idempotent: true\n  attempts: 3\n  on: [429]\n");
        var result = await Executor.ExecuteAsync(d, Get(instance.BaseUrl, "/v2/orders?customer=__fail503once"), await Channel(instance), Deadline.After(TimeSpan.FromSeconds(10)), "c", CancellationToken.None);
        Assert.Equal(503, result.Status);
    }

    private sealed class AlwaysBroker(int status) : IBroker
    {
        public int Calls;
        public string BrokerType => "fake";
        public BrokerCapabilities Capabilities { get; } = new(new HashSet<string>());
        public Task<DiscoveryResult> DiscoverAsync(ServiceInstance instance, CancellationToken ct) => throw new NotSupportedException();
        public Task<BrokerResult> ExecuteAsync(RenderedRequest request, IAuthenticatedChannel channel, string correlationId, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new BrokerResult(status, new Dictionary<string, string[]>(), Array.Empty<byte>()));
        }
    }

    /// <summary>Answers each read from a script, so a test can cross the deadline between two reads.</summary>
    private sealed class ScriptedClock(params DateTimeOffset[] reads) : TimeProvider
    {
        private int _next;
        public override DateTimeOffset GetUtcNow() => reads[Math.Min(_next++, reads.Length - 1)];
    }

    [Fact]
    public async Task Deadline_PassingBetweenTwoReads_DoesNotBreakTheTimer()
    {
        // A second read of Remaining would be negative; the executor must time the call from the one it checked.
        var at = DateTimeOffset.UtcNow;
        var deadline = new Deadline(at, new ScriptedClock(at - TimeSpan.FromSeconds(1), at + TimeSpan.FromMilliseconds(5)));
        var result = await new OperationExecutor(new AlwaysBroker(200)).ExecuteAsync(Descriptor(""), Get("http://x", "/"), null!, deadline, "corr-1", CancellationToken.None);
        Assert.Equal(200, result.Status);
    }

    [Fact]
    public async Task Deadline_AlreadyPassed_SourceTimeoutWithoutCalling()
    {
        var at = DateTimeOffset.UtcNow;
        var broker = new AlwaysBroker(200);
        var ex = await Assert.ThrowsAsync<SourceTimeoutException>(() =>
            new OperationExecutor(broker).ExecuteAsync(Descriptor(""), Get("http://x", "/"), null!, new Deadline(at, new ScriptedClock(at + TimeSpan.FromMilliseconds(5))), "corr-1", CancellationToken.None));
        Assert.Equal("corr-1", ex.CorrelationId);
        Assert.Equal(0, broker.Calls);
    }

    [Fact]
    public async Task Retry_StopsAtDeadline()
    {
        var broker = new AlwaysBroker(503);
        var d = Descriptor("retry:\n  idempotent: true\n  attempts: 10\n  on: [503]\n");
        var watch = Stopwatch.StartNew();
        var result = await new OperationExecutor(broker).ExecuteAsync(d, Get("http://x", "/"), null!, Deadline.After(TimeSpan.FromSeconds(1)), "c", CancellationToken.None);
        Assert.Equal(503, result.Status);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.5), $"took {watch.Elapsed}");
        Assert.InRange(broker.Calls, 2, 9);
    }

    [Fact]
    public async Task Network_Refused_SourceException()
    {
        var instance = Instance("http://127.0.0.1:1");
        var ex = await Assert.ThrowsAsync<SourceException>(() => Executor.ExecuteAsync(
            Descriptor(""), Get(instance.BaseUrl, "/v2/orders?key=hidden"), Channel(instance).Result, Deadline.After(TimeSpan.FromSeconds(10)), "corr-1", CancellationToken.None));
        Assert.Null(ex.Status);
        Assert.Contains("network", ex.Message);
        Assert.DoesNotContain("hidden", ex.Message);
        Assert.Equal("corr-1", ex.CorrelationId);
    }

    [Fact]
    public async Task Network_RetriedWhenListed()
    {
        var instance = Instance("http://127.0.0.1:1");
        var d = Descriptor("retry:\n  idempotent: true\n  attempts: 2\n  on: [network]\n");
        await Assert.ThrowsAsync<SourceException>(() => Executor.ExecuteAsync(d, Get(instance.BaseUrl, "/"), Channel(instance).Result, Deadline.After(TimeSpan.FromSeconds(10)), "c", CancellationToken.None));
    }
}
