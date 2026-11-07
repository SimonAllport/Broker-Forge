using System.Net;
using System.Web;
using BrokerForge.Contracts;
using BrokerForge.Host.Config;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BrokerForge.Contract.Tests;

/// <summary>
/// A design that cannot be read or validated, or a Dataverse that cannot be read, leaves the environment's file
/// instances and its other designs serving (HLD D-34, Review Focus 3, final review I1). What only the missing design
/// could have resolved gets a ConfigurationError carrying the failure, never "not mapped".
/// </summary>
public partial class EnvironmentConfigTests
{
    private const string Unreadable = "ext_/brokerforge/WarehouseAPI/design.xml: not valid XML (unexpected end of file)";

    private static BrokerForge.Connections.ConnectionDesign BrokenWarehouse() => Warehouse() with
    {
        Descriptors = new Dictionary<string, string>(Warehouse().Descriptors) { ["listItems"] = "operation: listItems\nversion: 1\nbinds: Nonsense\n" },
    };

    private async Task AssertOrdersServe(HostFactory host)
    {
        var (status, response, error) = await Run(host, List("ext_order", "OrdersAPI", EnvA));
        Assert.True(status == 200, error?.Message);
        Assert.Equal(50, response!.Rows.Count);
    }

    private static void AssertDesignFailure((int Status, CanonicalResponse? Response, ErrorPayload? Error) result, params string[] parts)
    {
        Assert.Equal(500, result.Status);
        Assert.Equal("ConfigurationError", result.Error!.Error);
        Assert.Contains(EnvA, result.Error.Message);
        foreach (var part in parts)
            Assert.Contains(part, result.Error.Message);
        Assert.DoesNotContain("not mapped", result.Error.Message);
    }

    [Fact]
    public async Task FileSourceServesWhileDesignInvalid()
    {
        _designs.Add(EnvA, BrokenWarehouse(), At(mock.BaseUrl));
        await KeyFor(EnvA);
        var host = Host();
        await AssertOrdersServe(host);
        AssertDesignFailure(await Run(host, List("ext_item", "WarehouseAPI", EnvA)), "WarehouseAPI/listItems.yaml (design v3)", "Table ext_item");
        Assert.Equal(1, _sent.Value);
    }

    [Fact]
    public async Task FileSourceServesWhileDesignUnreadable()
    {
        _designs.Fail(EnvA, Unreadable);
        var host = Host();
        await AssertOrdersServe(host);
        AssertDesignFailure(await Run(host, List("ext_item", "WarehouseAPI", EnvA)), Unreadable);
    }

    [Fact]
    public async Task OtherDesignsServeWhileOneIsInvalid()
    {
        _designs.Add(EnvA, Warehouse(), At(mock.BaseUrl));
        _designs.Add(EnvA, TicketsDesign() with { Auth = TicketsDesign().Auth with { Mode = "Colour" } }, At(mock.BaseUrl));
        await KeyFor(EnvA);
        var host = Host();
        var (status, response, error) = await Run(host, List("ext_item", "WarehouseAPI", EnvA));
        Assert.True(status == 200, error?.Message);
        Assert.NotEmpty(response!.Rows);
        AssertDesignFailure(await Run(host, List("ext_ticket", "TicketsAPI", EnvA, Tickets.AliceOid)), "Instances:TicketsAPI:Auth:Mode");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FileSourceServesWhileDataverseDownCold(bool transient)
    {
        var reason = $"Could not read connection designs for environment {EnvA} from Dataverse: timed out after 30 s.";
        _designs.Down = transient ? new DesignSourceUnavailableException(reason) : new BrokerForge.Abstractions.ConfigurationException(reason);
        var host = Host();
        await AssertOrdersServe(host);
        AssertDesignFailure(await Run(host, List("ext_item", "WarehouseAPI", EnvA)), reason);
    }

    [Fact]
    public async Task UnknownCustomApiGetsDesignFailures()
    {
        _designs.Fail(EnvA, Unreadable);
        var host = Host();
        var request = HostFactory.Request(null, "ext_CountItems", null);
        request.EnvironmentId = EnvA;
        AssertDesignFailure(await Run(host, request), Unreadable, "ext_CountItems");

        // An environment whose designs all loaded still says the message is unknown.
        request.EnvironmentId = EnvB;
        var (status, _, error) = await Run(host, request);
        Assert.Equal(400, status);
        Assert.Equal(nameof(UnsupportedQueryException), error!.Error);
    }

    [Fact]
    public async Task UnknownConnectInstanceGetsDesignFailures()
    {
        _designs.Fail(EnvA, Unreadable);
        var browser = Host().CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        browser.DefaultRequestHeaders.Add(ConnectTests.OidHeader, Tickets.AliceOid);

        var page = await browser.GetAsync($"/connect/{EnvA}/TicketsAPI");
        Assert.Equal(HttpStatusCode.InternalServerError, page.StatusCode);
        Assert.Contains(Unreadable, HttpUtility.HtmlDecode(await page.Content.ReadAsStringAsync()));
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync($"/connect/{EnvB}/TicketsAPI")).StatusCode);
    }
}
