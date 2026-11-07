using System.Net;
using System.Net.Sockets;
using BrokerForge.SwaggerForge;

namespace SwaggerForge.Tests;

public class DraftServiceTests
{
    internal static readonly DraftOptions Options = new("OrdersAPI", "ext");

    internal static string Text(string fixture) => File.ReadAllText(DrafterTests.Fixture(fixture));

    internal static DraftReview Review(string fixture, IReadOnlyDictionary<string, string>? answers = null, Func<string, string>? edit = null)
    {
        var text = Text(fixture);
        if (edit != null)
        {
            var edited = edit(text);
            Assert.NotEqual(text, edited);
            text = edited;
        }
        return DraftService.Review(text, Options, answers ?? DrafterTests.AnswersFor(fixture));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html><body>hi</body></html>")]
    [InlineData("openapi: 3.0.3\npaths:\n  /x: [unclosed")]
    public void Review_NotOpenApi_OneProblemNoResult(string text)
    {
        var r = DraftService.Review(text, Options, new Dictionary<string, string>());
        Assert.Single(r.Problems);
        Assert.Empty(r.Endpoints);
        Assert.Null(r.Result);
        Assert.False(r.Ready);
    }

    [Fact]
    public void Review_TooLong_Problem()
    {
        var r = DraftService.Review(new string(' ', DraftService.MaxDefinitionLength + 1), Options, new Dictionary<string, string>());
        Assert.Contains("2000000", Assert.Single(r.Problems));
        Assert.Empty(r.Endpoints);
    }

    [Theory]
    [InlineData("http://127.0.0.1:1/order.yaml")]
    [InlineData("../secrets.yaml")]
    public void Review_ExternalRef_ProblemAndNothingFetched(string reference)
    {
        var r = Review("orders-openapi.yaml", edit: t => t.Replace("{ $ref: \"#/components/schemas/Order\" }", $"{{ $ref: \"{reference}\" }}"));
        Assert.Contains(r.Problems, p => p.Contains(reference));
        Assert.Null(r.Result);
    }

    [Fact]
    public void Review_ExternalRefToListener_NoRequestMade()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var hit = listener.GetContextAsync();

        var url = $"http://127.0.0.1:{port}/order.yaml";
        var r = Review("orders-openapi.yaml", edit: t => t.Replace("{ $ref: \"#/components/schemas/Order\" }", $"{{ $ref: \"{url}\" }}"));

        Assert.Contains(r.Problems, p => p.Contains(url));
        Assert.False(hit.Wait(TimeSpan.FromMilliseconds(500)), "the external reference was fetched");
    }

    [Fact]
    public void Review_OperationUnticked_NotUsedAndLeftOut()
    {
        var answers = new Dictionary<string, string>(DrafterTests.AnswersFor("orders-openapi.yaml")) { ["op.deleteOrder.use"] = "no" };
        var r = Review("orders-openapi.yaml", answers);
        Assert.Equal(EndpointStatus.NotUsed, Assert.Single(r.Endpoints, e => e.Operation == "deleteOrder").Status);
        Assert.True(r.Ready, string.Join("; ", r.Problems.Concat(r.Endpoints.Select(e => $"{e.Operation} {e.Status}"))));
        Assert.DoesNotContain("deleteOrder", r.Result!.Descriptors.Keys);
        Assert.Contains("getOrder", r.Result.Descriptors.Keys);
        Assert.Equal("Delete", r.Endpoints.Single(e => e.Operation == "deleteOrder").Becomes);
    }

    [Fact]
    public void Review_UnknownAnswer_ListedUnusedNotApplied()
    {
        var answers = new Dictionary<string, string>(DrafterTests.AnswersFor("orders-openapi.yaml")) { ["column.$.nothere.name"] = "ext_x" };
        var r = Review("orders-openapi.yaml", answers);
        Assert.Contains("column.$.nothere.name", r.UnusedAnswers);
        Assert.DoesNotContain(r.Table!.Columns, c => c.LogicalName == "ext_x");
    }

    [Fact]
    public void Review_SkippedOperation_Unsupported()
    {
        var r = Review("unsupported.yaml");
        var variant = Assert.Single(r.Endpoints, e => e.Path == "/widgets/{widgetId}/variant");
        Assert.Equal(EndpointStatus.Unsupported, variant.Status);
        Assert.Contains("oneOf", variant.Reason);
        Assert.Equal(4, r.Endpoints.Count(e => e.Status == EndpointStatus.Unsupported));
    }

    [Fact]
    public void Review_NoEndpointTicked_NotReady()
    {
        var answers = new Dictionary<string, string>(DrafterTests.AnswersFor("orders-openapi.yaml"));
        foreach (var op in new[] { "listOrders", "getOrder", "createOrder", "updateOrder", "deleteOrder" })
            answers[$"op.{op}.use"] = "no";
        var r = Review("orders-openapi.yaml", answers);
        Assert.All(r.Endpoints, e => Assert.Equal(EndpointStatus.NotUsed, e.Status));
        Assert.Null(r.Result);
    }
}
