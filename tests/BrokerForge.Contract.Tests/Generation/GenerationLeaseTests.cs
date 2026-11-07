using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using BrokerForge.Auth.Tests;
using BrokerForge.Host.Generation;

namespace BrokerForge.Contract.Tests.Generation;

/// <summary>
/// The lease that keeps one Generate per environment and instance across host processes (plan 4 task 3, D-36). Every
/// implementation passes the same contract.
/// </summary>
public abstract class GenerationLeaseContract
{
    protected const string Env = "00000000-0000-0000-0000-00000000000a";
    protected static readonly TimeSpan Duration = TimeSpan.FromMinutes(20);
    protected readonly ConnectTests.MutableTime Time = new() { Now = new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero) };

    protected abstract IGenerationLease Lease { get; }

    [Fact]
    public async Task Lease_SecondHolderRefused()
    {
        Assert.NotNull(await Lease.TryAcquireAsync(Env, "WarehouseAPI", Duration, CancellationToken.None));
        Assert.Null(await Lease.TryAcquireAsync(Env, "WarehouseAPI", Duration, CancellationToken.None));
        Assert.NotNull(await Lease.TryAcquireAsync(Env, "InventoryAPI", Duration, CancellationToken.None));
    }

    [Fact]
    public async Task Lease_ExpiredTakenOver()
    {
        var first = await Lease.TryAcquireAsync(Env, "WarehouseAPI", Duration, CancellationToken.None);
        Time.Now += Duration + TimeSpan.FromSeconds(1);
        var second = await Lease.TryAcquireAsync(Env, "WarehouseAPI", Duration, CancellationToken.None);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        // The first holder, back from wherever it was, cannot free the new holder's lease.
        await Lease.ReleaseAsync(Env, "WarehouseAPI", first!, CancellationToken.None);
        Assert.Null(await Lease.TryAcquireAsync(Env, "WarehouseAPI", Duration, CancellationToken.None));
    }

    [Fact]
    public async Task Lease_ReleaseByOtherTokenIgnored()
    {
        Assert.NotNull(await Lease.TryAcquireAsync(Env, "WarehouseAPI", Duration, CancellationToken.None));
        await Lease.ReleaseAsync(Env, "WarehouseAPI", "not-the-holder", CancellationToken.None);
        Assert.Null(await Lease.TryAcquireAsync(Env, "WarehouseAPI", Duration, CancellationToken.None));
    }

    [Fact]
    public async Task Lease_ReleasedThenAcquiredAgain()
    {
        var token = await Lease.TryAcquireAsync(Env, "WarehouseAPI", Duration, CancellationToken.None);
        await Lease.ReleaseAsync(Env, "WarehouseAPI", token!, CancellationToken.None);
        Assert.NotNull(await Lease.TryAcquireAsync(Env, "WarehouseAPI", Duration, CancellationToken.None));
    }

    [Fact]
    public async Task Lease_KeyIgnoresCase()
    {
        Assert.NotNull(await Lease.TryAcquireAsync(Env.ToUpperInvariant(), "WarehouseAPI", Duration, CancellationToken.None));
        Assert.Null(await Lease.TryAcquireAsync(Env, "warehouseapi", Duration, CancellationToken.None));
    }
}

public sealed class InMemoryGenerationLeaseTests : GenerationLeaseContract
{
    public InMemoryGenerationLeaseTests() => Lease = new InMemoryGenerationLease(Time);
    protected override IGenerationLease Lease { get; }
}

[Collection(AzuriteCollection.Name)]
public sealed class TableGenerationLeaseTests : GenerationLeaseContract
{
    public TableGenerationLeaseTests(AzuriteFixture _)
    {
        var table = new TableServiceClient(AzuriteFixture.ConnectionString).GetTableClient("bflease" + Guid.NewGuid().ToString("N")[..12]);
        table.CreateIfNotExists();
        Lease = new TableGenerationLease(table, Time);
    }

    protected override IGenerationLease Lease { get; }

    [Fact]
    public async Task Lease_TwoHostsOneTable_SecondRefused()
    {
        // Two leases on the same table stand for two host replicas.
        var table = new TableServiceClient(AzuriteFixture.ConnectionString).GetTableClient("bflease" + Guid.NewGuid().ToString("N")[..12]);
        table.CreateIfNotExists();
        var a = new TableGenerationLease(table, Time);
        var b = new TableGenerationLease(table, Time);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => (i % 2 == 0 ? a : b).TryAcquireAsync(Env, "WarehouseAPI", Duration, CancellationToken.None)));
        Assert.Single(results, r => r != null);
    }
}

/// <summary>Which lease a host gets (final review M4a), and a lease store that fails (M4b).</summary>
public sealed class GenerationLeaseSetupTests
{
    private static Microsoft.Extensions.Configuration.IConfiguration Config(params (string, string)[] values) =>
        new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Item1, v.Item2))).Build();

    [Fact]
    public void Create_NoTableWithDesigns_Throws()
    {
        // A host with designs may run several replicas: an in-memory lease would not keep them apart.
        var ex = Assert.Throws<BrokerForge.Abstractions.ConfigurationException>(() =>
            GenerationLeases.Create(Config(), new Azure.Identity.DefaultAzureCredential(), TimeProvider.System, designs: true));
        Assert.Contains("Credentials:TableEndpoint", ex.Message);
    }

    [Fact]
    public void Create_NoTableWithDesignsInMemoryAsked_InMemory() =>
        Assert.IsType<InMemoryGenerationLease>(GenerationLeases.Create(Config(("Credentials:InMemory", "true")), new Azure.Identity.DefaultAzureCredential(), TimeProvider.System, designs: true));

    [Fact]
    public void Create_NoTableNoDesigns_InMemory() =>
        Assert.IsType<InMemoryGenerationLease>(GenerationLeases.Create(Config(), new Azure.Identity.DefaultAzureCredential(), TimeProvider.System, designs: false));

    private sealed class Unavailable : IGenerationLease
    {
        public Task<string?> TryAcquireAsync(string environmentId, string instance, TimeSpan duration, CancellationToken ct) =>
            throw new Azure.RequestFailedException(503, "table storage unavailable");
        public Task ReleaseAsync(string environmentId, string instance, string token, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task Jobs_LeaseStorageFails_BusyNamingIt()
    {
        var jobs = new GenerationJobs(BrokerForge.Contract.Tests.Admin.AdminApiTests.Trust, new BrokerForge.Contract.Tests.Admin.AdminApiTests.Clients(new BrokerForge.Contract.Tests.Admin.AdminDataverse()),
            new BrokerForge.Contract.Tests.Admin.AdminApiTests.Configs(), new LoggingGenerationProgress(Microsoft.Extensions.Logging.Abstractions.NullLogger<LoggingGenerationProgress>.Instance),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GenerationJobs>.Instance, null, new Unavailable());
        var row = BrokerForge.Contract.Tests.Admin.DesignBuilderTests.Inventory();
        var design = BrokerForge.Host.Admin.DesignBuilder.Build(row, BrokerForge.Host.Admin.DesignBuilder.Review(row));
        var ex = await Assert.ThrowsAsync<GenerationBusyException>(() => jobs.StartAsync(new GenerationRequest(BrokerForge.Contract.Tests.Admin.AdminApiTests.Env, design,
            "BrokerForgeInventoryAPI", "brokerforgeext", null, null, null, "corr")));
        Assert.Contains("generation lock could not be taken", ex.Message);
        Assert.Contains("503", ex.Message);
    }
}
