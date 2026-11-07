using System.Text.Json.Nodes;
using BrokerForge.Auth.Credentials;
using BrokerForge.Contracts;
using BrokerForge.Host.Admin;
using BrokerForge.Host.Generation;
using BrokerForge.Host.Ingress;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BrokerForge.Contract.Tests.Admin;

/// <summary>bf_Generate starts a background job and reports it on the connection row (plan 4 task 4).</summary>
public class GenerateApiTests
{
    private readonly AdminDataverse _dv = new();
    private readonly GenerationJobs _jobs;
    private readonly AdminApi _api;

    public GenerateApiTests()
    {
        var clients = new AdminApiTests.Clients(_dv);
        var progress = new ConnectionRowProgress(new LoggingGenerationProgress(NullLogger<LoggingGenerationProgress>.Instance), AdminApiTests.Trust, clients,
            NullLogger<ConnectionRowProgress>.Instance);
        _jobs = new GenerationJobs(AdminApiTests.Trust, clients, new AdminApiTests.Configs(), progress, NullLogger<GenerationJobs>.Instance);
        _api = new AdminApi(clients, _jobs, new InMemoryInstanceSecretStore(), new ConfigurationBuilder().Build());
    }

    private static CanonicalRequest Generate(Guid id, string? fingerprint = null, string? oid = AdminDataverse.AdminOid)
    {
        var request = AdminApiTests.Request("bf_Generate", id, oid);
        if (fingerprint != null)
            request.Inputs!.Add(new Cell { Col = "Fingerprint", Type = CellTypes.String, Value = fingerprint });
        return request;
    }

    private async Task<string> Fingerprint(Guid id) =>
        (string)JsonNode.Parse(AdminApiTests.Output(await _api.HandleAsync(AdminApiTests.Request("bf_ReadDefinition", id), CancellationToken.None), "Review"))!["preview"]!["fingerprint"]!;

    private async Task<GenerationJob> Started(Guid id, string? fingerprint = null)
    {
        var response = await _api.HandleAsync(Generate(id, fingerprint), CancellationToken.None);
        return await _jobs.WaitAsync(Guid.Parse(AdminApiTests.Output(response, "JobId")));
    }

    private JsonObject Row(Guid id) => _dv.Rows[id];

    private JsonObject Result(Guid id) => JsonNode.Parse((string)Row(id)["bf_generationresult"]!)!.AsObject();

    [Fact]
    public async Task Generate_NotAdmin_NoJob()
    {
        var id = _dv.AddRow(DesignBuilderTests.Inventory());
        await Assert.ThrowsAsync<CallerRejectedException>(() => _api.HandleAsync(Generate(id, oid: "00000000-0000-0000-0000-00000000a0ff"), CancellationToken.None));
        Assert.Empty(_dv.Fake.Writes);
        Assert.Empty(_dv.RowWrites);
    }

    [Fact]
    public async Task Generate_NotReady_RefusedNamingOpenQuestions()
    {
        var id = _dv.AddRow(DesignBuilderTests.Inventory(answers: "{}"));
        var ex = await Assert.ThrowsAsync<MappedSourceException>(() => _api.HandleAsync(Generate(id), CancellationToken.None));
        Assert.Equal(SourceErrorKinds.Validation, ex.Kind);
        Assert.Contains("op.listItems.paging", ex.Message);
        Assert.Empty(_dv.RowWrites);
    }

    [Fact]
    public async Task Generate_Ready_JobRunsRowShowsRunningThenSucceededWithPendingRole()
    {
        var id = _dv.AddRow(DesignBuilderTests.Inventory());
        var response = await _api.HandleAsync(Generate(id, await Fingerprint(id)), CancellationToken.None);
        Assert.Equal("Queued", AdminApiTests.Output(response, "State"));
        var job = await _jobs.WaitAsync(Guid.Parse(AdminApiTests.Output(response, "JobId")));

        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Contains(_dv.RowWrites, w => (string?)w.Body?["bf_generationstate"] == "Running");
        Assert.Equal("Succeeded", (string?)Row(id)["bf_generationstate"]);
        Assert.Equal(job.Id.ToString("D"), (string?)Row(id)["bf_generationjobid"]);
        Assert.NotNull(Row(id)["bf_generationstarted"]);
        Assert.NotNull(Row(id)["bf_generationfinished"]);
        var result = Result(id);
        Assert.NotEmpty(result["writes"]!.AsArray());
        Assert.Contains(result["pending"]!.AsArray(), p => ((string)p!).Contains("InventoryAPI user"));
        Assert.Equal(1, (int?)result["designVersion"]);
        Assert.False((bool)result["needsConfirmation"]!);
        // The host never takes the role step: it is left to an administrator (D-36).
        Assert.DoesNotContain(_dv.Fake.Writes, w => w.Path.Contains("AddPrivilegesRole"));
        Assert.Contains(_dv.Fake.Writes, w => w.Path == "EntityDefinitions" && w.Solution == "BrokerForgeInventoryAPI");
    }

    [Theory]
    [InlineData("column.$.sku.displayName", "Stock code")]   // changes the table metadata
    [InlineData("op.listItems.sortStable", "no")]            // changes only a descriptor (M1)
    public async Task Generate_AnswersChangedSinceReview_RefusedNothingWritten(string key, string value)
    {
        var id = _dv.AddRow(DesignBuilderTests.Inventory());
        var reviewed = await Fingerprint(id);
        var answers = JsonNode.Parse(DesignBuilderTests.InventoryAnswers)!.AsObject();
        answers[key] = value;
        Row(id)["bf_answers"] = answers.ToJsonString();

        var ex = await Assert.ThrowsAsync<MappedSourceException>(() => _api.HandleAsync(Generate(id, reviewed), CancellationToken.None));
        Assert.Equal(SourceErrorKinds.Conflict, ex.Kind);
        Assert.Contains("changed since", ex.Message);
        Assert.Empty(_dv.Fake.Writes);
    }

    [Fact]
    public async Task Generate_ReviewedFingerprint_Accepted()
    {
        var id = _dv.AddRow(DesignBuilderTests.Inventory());
        var job = await Started(id, await Fingerprint(id));
        Assert.Equal(JobState.Succeeded, job.State);
    }

    [Fact]
    public async Task Generate_LateReportOfOlderJob_DoesNotOverwriteNewerRow()
    {
        // M4c: a job that reports after its lease expired must not replace the status of a job started after it.
        var id = _dv.AddRow(DesignBuilderTests.Inventory());
        var newer = Guid.NewGuid();
        Row(id)["bf_generationjobid"] = newer.ToString("D");
        Row(id)["bf_generationstate"] = "Running";
        Row(id)["bf_generationstarted"] = DateTimeOffset.UtcNow.ToString("O");
        var progress = new ConnectionRowProgress(new LoggingGenerationProgress(NullLogger<LoggingGenerationProgress>.Instance), AdminApiTests.Trust,
            new AdminApiTests.Clients(_dv), NullLogger<ConnectionRowProgress>.Instance);
        await progress.ReportAsync(new GenerationJob(Guid.NewGuid(), AdminApiTests.Env, "InventoryAPI", JobState.Failed, Array.Empty<string>(), null, "timed out",
            DateTimeOffset.UtcNow.AddMinutes(-25), DateTimeOffset.UtcNow, "corr", ConnectionId: id), CancellationToken.None);
        Assert.Equal("Running", (string?)Row(id)["bf_generationstate"]);
        Assert.Equal(newer.ToString("D"), (string?)Row(id)["bf_generationjobid"]);
    }

    [Fact]
    public async Task Generate_DuplicateName_RefusedNothingStarted()
    {
        // Final review I2: two rows called the same (any case) would generate onto one instance, its keys and its lease.
        var id = _dv.AddRow(DesignBuilderTests.Inventory());
        _dv.AddRow(DesignBuilderTests.Inventory("inventoryapi") with { Id = Guid.Parse("00000000-0000-0000-0000-00000000c002") });
        var ex = await Assert.ThrowsAsync<MappedSourceException>(() => _api.HandleAsync(Generate(id), CancellationToken.None));
        Assert.Equal(SourceErrorKinds.Conflict, ex.Kind);
        Assert.Contains("2 connections are called InventoryAPI", ex.Message);
        Assert.Empty(_dv.Fake.Writes);
    }

    [Fact]
    public async Task Generate_Busy_Conflict()
    {
        var id = _dv.AddRow(DesignBuilderTests.Inventory());
        _dv.Fake.Delay = TimeSpan.FromMilliseconds(30);
        var first = await _api.HandleAsync(Generate(id), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<MappedSourceException>(() => _api.HandleAsync(Generate(id), CancellationToken.None));
        Assert.Equal(SourceErrorKinds.Conflict, ex.Kind);
        Assert.Contains("already running", ex.Message);
        await _jobs.WaitAsync(Guid.Parse(AdminApiTests.Output(first, "JobId")));
    }

    [Fact]
    public async Task Generate_ProgressWriteFails_JobResultUnchanged()
    {
        var id = _dv.AddRow(DesignBuilderTests.Inventory());
        _dv.FailRowWrites = true;
        var job = await Started(id);
        Assert.Equal(JobState.Succeeded, job.State);
        Assert.NotEmpty(job.Writes);
    }

    [Fact]
    public async Task Generate_RowResultHasNoDesignText()
    {
        var id = _dv.AddRow(DesignBuilderTests.Inventory());
        await Started(id);
        var written = string.Concat(_dv.RowWrites.Select(w => w.Body?.ToJsonString()));
        Assert.DoesNotContain("openapi", written, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/inv/v1/items", written);
    }

    [Fact]
    public async Task Generate_NoConnectionId_RowWritesNone()
    {
        // A job started without a connection row (the admin command) is reported only in the log.
        var progress = new ConnectionRowProgress(new LoggingGenerationProgress(NullLogger<LoggingGenerationProgress>.Instance), AdminApiTests.Trust,
            new AdminApiTests.Clients(_dv), NullLogger<ConnectionRowProgress>.Instance);
        await progress.ReportAsync(new GenerationJob(Guid.NewGuid(), AdminApiTests.Env, "InventoryAPI", JobState.Running, Array.Empty<string>(), null, null,
            DateTimeOffset.UtcNow, null, "corr"), CancellationToken.None);
        Assert.Empty(_dv.RowWrites);
    }
}
