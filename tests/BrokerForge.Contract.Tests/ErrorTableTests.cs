using BrokerForge.Contracts;

namespace BrokerForge.Contract.Tests;

/// <summary>One test per HLD 11 row in phase 1 scope.</summary>
[Collection(MockApiCollection.Name)]
public class ErrorTableTests(MockApiFixture mock) : HostTestBase(mock)
{
    [Fact]
    public async Task UndeclaredFilter_400UnsupportedQueryNamingColumn()
    {
        var request = List("ext_order", 10, Where("ext_name", Ops.Eq, "REF-0001"));
        var (status, _, error) = await Host.Execute(request);
        Assert.Equal(400, status);
        Assert.Equal("UnsupportedQueryException", error!.Error);
        Assert.Contains("ext_name", error.Message);
        Assert.Contains("eq", error.Message);
        Assert.Equal(request.CorrelationId, error.CorrelationId);
    }

    [Fact]
    public async Task UndeclaredSort_400NamingColumn()
    {
        var request = List("ext_order", 10);
        request.Query.OrderBy = new() { new OrderBy { Col = "ext_customerref" } };
        var (status, _, error) = await Host.Execute(request);
        Assert.Equal(400, status);
        Assert.Contains("ext_customerref", error!.Message);
    }

    [Fact]
    public async Task Mapped404_404NotFound()
    {
        var request = HostFactory.Request("ext_order", Messages.Retrieve);
        request.Id = Id("ord_9999");
        var (status, _, error) = await Host.Execute(request);
        Assert.Equal(404, status);
        Assert.Equal("NotFound", error!.Error);
    }

    [Fact]
    public async Task Unmapped500_502SourceExceptionWithCorrelationId()
    {
        var request = List("ext_order", 10, Where("ext_customerref", Ops.Eq, "__fail500"));
        var (status, _, error) = await Host.Execute(request);
        Assert.Equal(502, status);
        Assert.Equal("SourceException", error!.Error);
        Assert.Contains("500", error.Message);
        Assert.Equal(request.CorrelationId, error.CorrelationId);
    }

    [Fact]
    public async Task Unmapped418_502SourceException()
    {
        var (status, _, error) = await Host.Execute(List("ext_order", 10, Where("ext_customerref", Ops.Eq, "__teapot")));
        Assert.Equal(502, status);
        Assert.Equal("SourceException", error!.Error);
        Assert.Contains("418", error.Message);
    }

    [Fact]
    public async Task Timeout_504()
    {
        using var host = new HostFactory(Mock.BaseUrl, timeoutSeconds: 3);
        var (status, _, error) = await host.Execute(List("ext_order", 10, Where("ext_customerref", Ops.Eq, "__slow")));
        Assert.Equal(504, status);
        Assert.Equal("SourceTimeoutException", error!.Error);
    }

    [Fact]
    public async Task ShapeMismatch_502MappingNamingPath()
    {
        var (status, _, error) = await Host.Execute(List("ext_broken", 10));
        Assert.Equal(502, status);
        Assert.Equal("MappingException", error!.Error);
        Assert.Contains("$.data[*]", error.Message);
    }

    [Fact]
    public async Task RandomGuidRetrieve_404()
    {
        var request = HostFactory.Request("ext_order", Messages.Retrieve);
        request.Id = Guid.NewGuid().ToString();
        var (status, _, error) = await Host.Execute(request);
        Assert.Equal(404, status);
        Assert.Equal("NotFound", error!.Error);
    }

    [Fact]
    public async Task UnknownTable_400()
    {
        var (status, _, error) = await Host.Execute(List("ext_nope", 10));
        Assert.Equal(400, status);
        Assert.Contains("ext_nope", error!.Message);
    }

    [Fact]
    public async Task MessageWithoutOperation_400()
    {
        var request = HostFactory.Request("ext_broken", Messages.Delete);
        request.Id = Id("x");
        var (status, _, error) = await Host.Execute(request);
        Assert.Equal(400, status);
        Assert.Contains("Delete", error!.Message);
    }

    [Fact]
    public async Task InstanceTableMismatch_400()
    {
        var (status, _, _) = await Host.Execute(List("ext_order", 10, instance: "EchoAPI"));
        Assert.Equal(400, status);
    }
}
