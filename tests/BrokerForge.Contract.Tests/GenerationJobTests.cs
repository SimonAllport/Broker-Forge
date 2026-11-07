using BrokerForge.Abstractions;
using BrokerForge.Connections;
using BrokerForge.Generator;
using BrokerForge.Host.Config;
using BrokerForge.Host.Generation;
using BrokerForge.Host.Ingress;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Fake = BrokerForge.Generator.Tests.FakeDataverse;

namespace BrokerForge.Contract.Tests;

/// <summary>Background generation as the host's own identity (plan 3 task 5): one job per environment and instance.</summary>
public class GenerationJobTests
{
    private const string EnvA = "00000000-0000-0000-0000-00000000000a";
    private const string EnvB = "00000000-0000-0000-0000-00000000000b";
    private const string OrgA = "https://contoso-dev.crm4.dynamics.com";
    private const string OrgB = "https://contoso-test.crm4.dynamics.com";

    private sealed class Clients(Dictionary<string, Fake> fakes) : IGenerationClientFactory
    {
        public DataverseClient Create(string orgUrl) =>
            new(new HttpClient(fakes[orgUrl]) { BaseAddress = new Uri(orgUrl + "/api/data/v9.2/") }, _ => Task.FromResult("host-token"), false);
    }

    private sealed class Configs : IHostConfigProvider
    {
        public List<string> Invalidated { get; } = new();
        public Task<HostConfigStore> ForEnvironmentAsync(string environmentId, CancellationToken ct) => throw new NotSupportedException();
        public HostConfigStore Files => throw new NotSupportedException();
        public void Invalidate(string environmentId)
        {
            lock (Invalidated)
                Invalidated.Add(environmentId);
        }
    }

    private sealed class Progress : IGenerationProgress
    {
        public List<GenerationJob> Reports { get; } = new();
        public Task ReportAsync(GenerationJob job, CancellationToken ct)
        {
            lock (Reports)
                Reports.Add(job);
            return Task.CompletedTask;
        }
    }

    private readonly Dictionary<string, Fake> _fakes = new() { [OrgA] = Prepared(), [OrgB] = Prepared() };
    private readonly Configs _configs = new();
    private readonly Progress _progress = new();

    private static Fake Prepared()
    {
        var fake = new Fake();
        fake.SetVariable("bf_HostUrl", "https://bf-host.contoso.example");
        fake.SetVariable("bf_HostScope", "api://00000000-0000-0000-0000-0000000000c1/.default");
        return fake;
    }

    private GenerationJobs Jobs(TimeSpan? limit = null, IGenerationLease? lease = null) => new(
        Options.Create(new TrustOptions
        {
            AllowedCallers =
            {
                new AllowedCaller { ClientId = "a", EnvironmentId = EnvA, OrgUrl = OrgA },
                new AllowedCaller { ClientId = "b", EnvironmentId = EnvB, OrgUrl = OrgB },
                new AllowedCaller { ClientId = "c", EnvironmentId = "00000000-0000-0000-0000-00000000000c" },
            },
        }),
        new Clients(_fakes), _configs, _progress, NullLogger<GenerationJobs>.Instance, limit, lease);

    private static GenerationRequest Request(string env = EnvA, ConnectionDesign? design = null) =>
        new(env, design ?? EnvironmentConfigTests.Warehouse(), "BrokerForgeWarehouse", "brokerforgeext", "https://inventory.contoso.example", null, null, "corr-1");

    [Fact]
    public async Task Start_ReturnsAtOnceThenSucceeds()
    {
        _fakes[OrgA].Delay = TimeSpan.FromMilliseconds(20);
        var jobs = Jobs();
        var job = (await jobs.StartAsync(Request()));
        Assert.Equal(JobState.Queued, job.State);
        var done = await jobs.WaitAsync(job.Id);
        Assert.True(done.State == JobState.Succeeded, done.Failure);
        Assert.Equal(1, done.DesignVersion);
        Assert.Contains(done.Writes, w => w == "POST EntityDefinitions");
        Assert.Contains(_fakes[OrgA].Requests, r => r.Authorization == "Bearer host-token");
        Assert.Equal(done, jobs.Get(job.Id));
        Assert.Equal(new[] { JobState.Running, JobState.Succeeded }, _progress.Reports.Select(r => r.State).Distinct().Where(s => s != JobState.Queued));
    }

    [Fact]
    public async Task Start_SameInstanceTwice_SecondBusy()
    {
        _fakes[OrgA].Delay = TimeSpan.FromMilliseconds(50);
        var jobs = Jobs();
        var first = (await jobs.StartAsync(Request()));
        var ex = await Assert.ThrowsAsync<GenerationBusyException>(() => jobs.StartAsync(Request()));
        Assert.Contains("WarehouseAPI", ex.Message);
        await jobs.WaitAsync(first.Id);
        var again = (await jobs.StartAsync(Request()));
        Assert.Equal(JobState.Succeeded, (await jobs.WaitAsync(again.Id)).State);
    }

    [Fact]
    public async Task Jobs_TwoHostsShareLease_SecondBusy()
    {
        // Two GenerationJobs with one lease stand for two host processes: neither knows the other's jobs.
        _fakes[OrgA].Delay = TimeSpan.FromMilliseconds(50);
        var lease = new InMemoryGenerationLease(TimeProvider.System);
        var replicaA = Jobs(lease: lease);
        var replicaB = Jobs(lease: lease);
        var first = await replicaA.StartAsync(Request(), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<GenerationBusyException>(() => replicaB.StartAsync(Request(), CancellationToken.None));
        Assert.Contains("another host", ex.Message);
        await replicaA.WaitAsync(first.Id);
        var again = await replicaB.StartAsync(Request(), CancellationToken.None);
        Assert.Equal(JobState.Succeeded, (await replicaB.WaitAsync(again.Id)).State);
    }

    [Fact]
    public async Task Jobs_LeaseReleasedAfterFailure()
    {
        var lease = new InMemoryGenerationLease(TimeProvider.System);
        _fakes[OrgA] = new Fake();
        var jobs = Jobs(lease: lease);
        var failed = await jobs.WaitAsync((await jobs.StartAsync(Request(), CancellationToken.None)).Id);
        Assert.Equal(JobState.Failed, failed.State);
        Assert.NotNull(await lease.TryAcquireAsync(EnvA, "WarehouseAPI", TimeSpan.FromMinutes(1), CancellationToken.None));
    }

    [Fact]
    public async Task Start_SameInstanceOtherCase_Busy()
    {
        _fakes[OrgA].Delay = TimeSpan.FromMilliseconds(50);
        var jobs = Jobs();
        var first = (await jobs.StartAsync(Request()));
        var lower = EnvironmentConfigTests.Warehouse() with { Instance = "warehouseapi" };
        await Assert.ThrowsAsync<GenerationBusyException>(() => jobs.StartAsync(Request(design: lower)));
        await jobs.WaitAsync(first.Id);
    }

    [Fact]
    public async Task Job_NetworkFailure_FailedNamingStep()
    {
        _fakes[OrgA].ThrowWrite = 4;
        var jobs = Jobs();
        var done = await jobs.WaitAsync((await jobs.StartAsync(Request())).Id);
        Assert.Equal(JobState.Failed, done.State);
        Assert.Contains("injected network failure", done.Failure);
        Assert.Contains("step", done.Failure);
        Assert.Equal(3, done.Writes.Count);
    }

    [Fact]
    public async Task Start_SameNameTwoEnvironments_BothRun()
    {
        _fakes[OrgA].Delay = _fakes[OrgB].Delay = TimeSpan.FromMilliseconds(20);
        var jobs = Jobs();
        var a = (await jobs.StartAsync(Request(EnvA)));
        var b = (await jobs.StartAsync(Request(EnvB)));
        Assert.Equal(JobState.Succeeded, (await jobs.WaitAsync(a.Id)).State);
        Assert.Equal(JobState.Succeeded, (await jobs.WaitAsync(b.Id)).State);
        Assert.Contains(_fakes[OrgB].Writes, w => w.Path == "EntityDefinitions");
    }

    [Fact]
    public async Task Job_GeneratorThrows_FailedWithMessage()
    {
        _fakes[OrgA].FailWrite = 4;
        var jobs = Jobs();
        var done = await jobs.WaitAsync((await jobs.StartAsync(Request())).Id);
        Assert.Equal(JobState.Failed, done.State);
        Assert.Contains("injected failure", done.Failure);
        Assert.Contains("step", done.Failure);
        Assert.False(done.NeedsConfirmation);
        Assert.Equal(3, done.Writes.Count);
        Assert.Empty(_configs.Invalidated);
    }

    [Fact]
    public async Task Job_NeedsConfirmation_FailedNamingFingerprint()
    {
        var jobs = Jobs();
        await jobs.WaitAsync((await jobs.StartAsync(Request())).Id);
        var design = EnvironmentConfigTests.Warehouse();
        var changed = design with { Schema = design.Schema.Replace("displayName: SKU", "displayName: Stock code") };
        var preview = await jobs.PreviewAsync(Request(design: changed), CancellationToken.None);
        var fingerprint = Assert.Single(preview.Changes.Changes.Select(_ => preview.Changes.Fingerprint).Distinct());
        var refused = await jobs.WaitAsync((await jobs.StartAsync(Request(design: changed))).Id);
        Assert.Equal(JobState.Failed, refused.State);
        Assert.Contains(fingerprint, refused.Failure);
        Assert.True(refused.NeedsConfirmation);
        var confirmed = await jobs.WaitAsync((await jobs.StartAsync(Request(design: changed) with { ConfirmedFingerprint = fingerprint })).Id);
        Assert.True(confirmed.State == JobState.Succeeded, confirmed.Failure);
        Assert.Equal(2, confirmed.DesignVersion);
        Assert.False(confirmed.NeedsConfirmation);
    }

    [Fact]
    public async Task Job_Timeout_FailedTyped()
    {
        _fakes[OrgA].Delay = TimeSpan.FromSeconds(5);
        var jobs = Jobs(TimeSpan.FromMilliseconds(100));
        var done = await jobs.WaitAsync((await jobs.StartAsync(Request())).Id);
        Assert.Equal(JobState.Failed, done.State);
        Assert.StartsWith("Generation timed out after", done.Failure);
        Assert.Contains("writes made:", done.Failure);
    }

    [Fact]
    public async Task Job_Succeeded_InvalidatesThatEnvironmentOnly()
    {
        var jobs = Jobs();
        await jobs.WaitAsync((await jobs.StartAsync(Request(EnvB))).Id);
        Assert.Equal(new[] { EnvB }, _configs.Invalidated);
    }

    [Fact]
    public async Task Progress_NeverContainsDesignText()
    {
        var jobs = Jobs();
        _fakes[OrgA].FailWrite = 2;
        await jobs.WaitAsync((await jobs.StartAsync(Request())).Id);
        var openApi = EnvironmentConfigTests.Warehouse().OpenApi;
        Assert.All(_progress.Reports, r => Assert.DoesNotContain(openApi[..Math.Min(40, openApi.Length)], (r.Failure ?? "") + string.Join("", r.Writes)));
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-00000000000c")]
    [InlineData("00000000-0000-0000-0000-0000000000ff")]
    public async Task Start_UnknownEnvironment_ConfigurationException(string env)
    {
        var ex = await Assert.ThrowsAsync<ConfigurationException>(() => Jobs().StartAsync(Request(env)));
        Assert.Contains(env, ex.Message);
    }

    [Fact]
    public async Task Start_NoHostUrlVariable_FailedNamingIt()
    {
        _fakes[OrgA] = new Fake();
        var jobs = Jobs();
        var failed = await jobs.WaitAsync((await jobs.StartAsync(Request())).Id);
        Assert.Equal(JobState.Failed, failed.State);
        Assert.Contains("bf_HostUrl", failed.Failure);
        Assert.Empty(_fakes[OrgA].Writes);
    }

    private async Task<(int Code, string Output)> Command(params string[] args)
    {
        var output = new StringWriter();
        var code = await BrokerForge.Host.Admin.GenerateCommand.RunAsync(args, Jobs(), output, CancellationToken.None);
        return (code, output.ToString());
    }

    private static string DesignFile(ConnectionDesign design)
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("bf-design-").FullName, "design.json");
        File.WriteAllText(file, DesignSerializer.Serialize(design));
        return file;
    }

    [Fact]
    public async Task Command_DesignFile_GeneratesWithoutPrintingTheDesign()
    {
        var design = EnvironmentConfigTests.Warehouse();
        var (code, output) = await Command("--environment", EnvA, "--instance", "WarehouseAPI", "--solution", "BrokerForgeWarehouse", "--publisher", "brokerforgeext",
            "--design-file", DesignFile(design), "--base-url", "https://inventory.contoso.example");
        Assert.True(code == 3, output);
        Assert.Contains("Succeeded", output);
        Assert.Contains("design v1", output);
        Assert.Contains("needs an administrator: create role WarehouseAPI user with Read", output);
        Assert.DoesNotContain(design.OpenApi[..Math.Min(40, design.OpenApi.Length)], output);
    }

    [Fact]
    public async Task Job_RoleLeftToAdmin_PendingNoRoleWrites()
    {
        var jobs = Jobs();
        var done = await jobs.WaitAsync((await jobs.StartAsync(Request())).Id);
        Assert.True(done.State == JobState.Succeeded, done.Failure);
        // The action and the role are both the administrator's (plan 5): the host can't create a Custom API either.
        Assert.Equal(2, done.Pending.Count);
        Assert.Contains(done.Pending, p => p.StartsWith("add action ", StringComparison.Ordinal));
        Assert.Contains(done.Pending, p => p.StartsWith("create role WarehouseAPI user with ", StringComparison.Ordinal));
        Assert.DoesNotContain(_fakes[OrgA].Writes, w => w.Path.StartsWith("roles", StringComparison.Ordinal) || w.Path.StartsWith("customapis", StringComparison.Ordinal));
        var admin = await jobs.WaitAsync((await jobs.StartAsync(Request() with { ApplyRole = true })).Id);
        Assert.Empty(admin.Pending);
        Assert.Contains(_fakes[OrgA].Writes, w => w.Path == "roles");
    }

    [Fact]
    public async Task Command_WithRole_AppliesRoleExit0()
    {
        var (code, output) = await Command("--environment", EnvA, "--instance", "WarehouseAPI", "--solution", "BrokerForgeWarehouse", "--publisher", "brokerforgeext",
            "--design-file", DesignFile(EnvironmentConfigTests.Warehouse()), "--with-role");
        Assert.True(code == 0, output);
        Assert.DoesNotContain("needs an administrator", output);
        Assert.Contains(_fakes[OrgA].Writes, w => w.Path == "roles");
    }

    [Fact]
    public async Task Command_StaleConfirm_Exit4NothingWritten()
    {
        var design = EnvironmentConfigTests.Warehouse();
        // As an administrator, so the role and the action are done too and the published design has nothing left to apply.
        await Command("--environment", EnvA, "--instance", "WarehouseAPI", "--solution", "BrokerForgeWarehouse", "--publisher", "brokerforgeext", "--design-file", DesignFile(design),
            "--with-role");
        var before = _fakes[OrgA].Writes.Count();
        var changed = DesignFile(design with { Schema = design.Schema.Replace("displayName: SKU", "displayName: Stock code") });
        var (code, output) = await Command("--environment", EnvA, "--instance", "WarehouseAPI", "--solution", "BrokerForgeWarehouse", "--publisher", "brokerforgeext",
            "--design-file", changed, "--confirm", "0000000000000000");
        Assert.True(code == 4, output);
        Assert.Equal(before, _fakes[OrgA].Writes.Count());
    }

    [Fact]
    public async Task Command_FromEnvironment_PreviewThenConfirm()
    {
        var design = EnvironmentConfigTests.Warehouse();
        // As an administrator, so the role and the action are done too and the published design has nothing left to apply.
        await Command("--environment", EnvA, "--instance", "WarehouseAPI", "--solution", "BrokerForgeWarehouse", "--publisher", "brokerforgeext", "--design-file", DesignFile(design),
            "--with-role");
        var before = _fakes[OrgA].Writes.Count();
        var (code, output) = await Command("--environment", EnvA, "--instance", "WarehouseAPI", "--solution", "BrokerForgeWarehouse", "--publisher", "brokerforgeext",
            "--from-environment", "--preview");
        Assert.True(code == 0, output);
        Assert.Contains("no changes", output);
        Assert.Equal(before, _fakes[OrgA].Writes.Count());

        var changed = DesignFile(design with { Schema = design.Schema.Replace("displayName: SKU", "displayName: Stock code") });
        (code, output) = await Command("--environment", EnvA, "--instance", "WarehouseAPI", "--solution", "BrokerForgeWarehouse", "--publisher", "brokerforgeext", "--design-file", changed);
        Assert.True(code == 4, output);
        var fingerprint = System.Text.RegularExpressions.Regex.Match(output, "--confirm ([0-9a-f]{16})").Groups[1].Value;
        Assert.Equal(before, _fakes[OrgA].Writes.Count());
        (code, output) = await Command("--environment", EnvA, "--instance", "WarehouseAPI", "--solution", "BrokerForgeWarehouse", "--publisher", "brokerforgeext",
            "--design-file", changed, "--confirm", fingerprint);
        Assert.True(code == 0, output); // done; the role and the action were already set
        Assert.Contains("design v2", output);
    }

    [Theory]
    [InlineData("--environment", EnvA, "--instance", "WarehouseAPI")]
    [InlineData("--environment", EnvA, "--instance", "WarehouseAPI", "--solution", "S", "--publisher", "P", "--from-environment", "--design-file", "x")]
    public async Task Command_BadArguments_Usage(params string[] args)
    {
        var (code, output) = await Command(args);
        Assert.Equal(2, code);
        Assert.StartsWith("usage: generate", output);
    }

    [Fact]
    public async Task Job_DescriptorsDisagreeWithSchema_FailedBeforeAnyWrite()
    {
        var design = EnvironmentConfigTests.Warehouse();
        var unbound = design with { Descriptors = design.Descriptors.Where(d => !d.Value.Contains("customapi:")).ToDictionary(d => d.Key, d => d.Value) };
        Assert.NotEqual(design.Descriptors.Count, unbound.Descriptors.Count);
        var jobs = Jobs();
        var failed = await jobs.WaitAsync((await jobs.StartAsync(Request(design: unbound))).Id);
        Assert.Equal(JobState.Failed, failed.State);
        Assert.Contains("no descriptor binds it", failed.Failure);
        Assert.Empty(_fakes[OrgA].Writes);
    }
}
