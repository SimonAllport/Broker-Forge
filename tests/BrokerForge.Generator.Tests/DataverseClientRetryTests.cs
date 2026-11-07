using System.Net;
using System.Text.Json.Nodes;
using BrokerForge.Generator;

namespace BrokerForge.Generator.Tests;

/// <summary>
/// Dataverse answers 429 when a service protection limit is hit or another customisation is publishing: the request was
/// not processed, and Retry-After says when to send it again (portal-5 final review I5, a PublishXml lost to a 429).
/// </summary>
public class DataverseClientRetryTests
{
    private sealed class Answers(params Func<HttpResponseMessage>[] answers) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answers[Math.Min(Calls++, answers.Length - 1)]());
    }

    private static HttpResponseMessage Busy(int? retryAfterSeconds)
    {
        var response = new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("""{"error":{"message":"busy"}}""") };
        if (retryAfterSeconds is { } s)
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(s));
        return response;
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.NoContent);

    private static (DataverseClient Client, List<TimeSpan> Waits) Client(Answers answers)
    {
        var waits = new List<TimeSpan>();
        var client = new DataverseClient(new HttpClient(answers) { BaseAddress = new Uri("https://contoso.crm4.dynamics.com/api/data/v9.2/") },
            _ => Task.FromResult("token"), false, (wait, _) => { waits.Add(wait); return Task.CompletedTask; });
        return (client, waits);
    }

    private static readonly JsonObject Publish = new() { ["ParameterXml"] = "<importexportxml><entities><entity>ext_item</entity></entities></importexportxml>" };

    [Fact]
    public async Task Busy_WaitsRetryAfterThenSendsAgain()
    {
        var answers = new Answers(() => Busy(7), Ok);
        var (client, waits) = Client(answers);
        await client.WriteAsync(HttpMethod.Post, "PublishXml", Publish, null, CancellationToken.None);
        Assert.Equal(2, answers.Calls);
        Assert.Equal(new[] { TimeSpan.FromSeconds(7) }, waits);
        Assert.Equal(new[] { "POST PublishXml" }, client.Writes);
    }

    [Fact]
    public async Task BusyWithoutRetryAfter_WaitsTenSeconds()
    {
        var (client, waits) = Client(new Answers(() => Busy(null), Ok));
        await client.WriteAsync(HttpMethod.Post, "PublishXml", Publish, null, CancellationToken.None);
        Assert.Equal(new[] { TimeSpan.FromSeconds(10) }, waits);
    }

    [Fact]
    public async Task LongRetryAfter_CappedAtOneMinute()
    {
        var (client, waits) = Client(new Answers(() => Busy(600), Ok));
        await client.WriteAsync(HttpMethod.Post, "PublishXml", Publish, null, CancellationToken.None);
        Assert.Equal(new[] { TimeSpan.FromMinutes(1) }, waits);
    }

    [Fact]
    public async Task StillBusyAfterThreeRetries_FailsNamingThe429()
    {
        var answers = new Answers(() => Busy(1));
        var (client, waits) = Client(answers);
        var ex = await Assert.ThrowsAsync<DataverseException>(() => client.WriteAsync(HttpMethod.Post, "PublishXml", Publish, null, CancellationToken.None));
        Assert.Contains("HTTP 429", ex.Message);
        Assert.Equal(4, answers.Calls);
        Assert.Equal(3, waits.Count);
        Assert.Empty(client.Writes);
    }

    [Fact]
    public async Task OtherFailure_NotRetried()
    {
        var answers = new Answers(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("down") });
        var (client, waits) = Client(answers);
        await Assert.ThrowsAsync<DataverseException>(() => client.WriteAsync(HttpMethod.Post, "PublishXml", Publish, null, CancellationToken.None));
        Assert.Equal(1, answers.Calls);
        Assert.Empty(waits);
    }
}
