using System.Text.Json.Nodes;
using BrokerForge.Auth.Credentials;
using BrokerForge.Contracts;
using BrokerForge.Generator;
using BrokerForge.Host.Admin;
using BrokerForge.Host.Config;
using BrokerForge.Host.Generation;
using BrokerForge.Host.Ingress;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BrokerForge.Contract.Tests.Admin;

/// <summary>The administrator check and bf_ReadDefinition (plan 4 task 2).</summary>
public class AdminApiTests
{
    internal const string Env = "00000000-0000-0000-0000-00000000000a";
    internal const string Org = "https://contoso-dev.crm4.dynamics.com";
    internal const string PublicUrl = "https://bf-host.contoso.example";

    internal sealed class Clients(AdminDataverse dv) : IGenerationClientFactory, IDelegatedDataverseFactory
    {
        public DataverseClient Create(string orgUrl, string bearerToken) =>
            new(new HttpClient(dv.Fake) { BaseAddress = new Uri(orgUrl + "/api/data/v9.2/") }, _ => Task.FromResult(bearerToken), false);

        public string? LastToken { get; private set; }
        public DataverseClient Create(string orgUrl) =>
            new(new HttpClient(dv.Fake) { BaseAddress = new Uri(orgUrl + "/api/data/v9.2/") }, _ => Task.FromResult(LastToken = "host-token"), false);
    }

    internal sealed class Configs : IHostConfigProvider
    {
        public Task<HostConfigStore> ForEnvironmentAsync(string environmentId, CancellationToken ct) => throw new NotSupportedException();
        public HostConfigStore Files => throw new NotSupportedException();
        public void Invalidate(string environmentId) { }
    }

    internal static IOptions<TrustOptions> Trust => Options.Create(new TrustOptions
    {
        AllowedCallers = { new AllowedCaller { ClientId = "a", EnvironmentId = Env, OrgUrl = Org } },
    });

    internal readonly AdminDataverse Dv = new();
    internal readonly InMemoryInstanceSecretStore Secrets = new();
    internal GenerationJobs Jobs { get; }
    internal AdminApi Api { get; }

    public AdminApiTests()
    {
        Jobs = new GenerationJobs(Trust, new Clients(Dv), new Configs(), new LoggingGenerationProgress(NullLogger<LoggingGenerationProgress>.Instance), NullLogger<GenerationJobs>.Instance);
        Api = new AdminApi(new Clients(Dv), Jobs, Secrets,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Connect:PublicBaseUrl"] = PublicUrl }).Build());
    }

    internal static CanonicalRequest Request(string message, Guid? connection, string? oid = AdminDataverse.AdminOid) => new()
    {
        CorrelationId = "corr-admin",
        EnvironmentId = Env,
        Message = message,
        Caller = new CallerContext { UserObjectId = oid },
        Inputs = connection is { } c ? new List<Cell> { new() { Col = "ConnectionId", Type = CellTypes.Guid, Value = c.ToString("D") } } : new List<Cell>(),
    };

    internal static string Output(CanonicalResponse response, string name) => Assert.Single(response.Outputs!, o => o.Col == name).Value!;

    private async Task<JsonObject> Read(Guid id, string? oid = AdminDataverse.AdminOid) =>
        JsonNode.Parse(Output(await Api.HandleAsync(Request("bf_ReadDefinition", id, oid), CancellationToken.None), "Review"))!.AsObject();

    private Guid Inventory(string? answers = null) => Dv.AddRow(DesignBuilderTests.Inventory(answers: answers));

    [Fact]
    public async Task Admin_NoObjectId_Refused()
    {
        var ex = await Assert.ThrowsAsync<CallerRejectedException>(() => Read(Inventory(), oid: null));
        Assert.Equal(AdminCheck.Refusal, ex.Message);
    }

    [Fact]
    public async Task Admin_CustomizerWithoutRole_Refused()
    {
        // A System Customizer holds prvWritebf_connection, so the platform lets the call through; the role is what counts.
        const string customizer = "00000000-0000-0000-0000-00000000a002";
        Dv.AddUser(customizer, "00000000-0000-0000-0000-0000000005c0");
        await Assert.ThrowsAsync<CallerRejectedException>(() => Read(Inventory(), customizer));
    }

    [Fact]
    public async Task Admin_DisabledUserWithRole_Refused()
    {
        // Final review I1: a user removed from the environment is disabled but keeps their role assignments.
        const string former = "00000000-0000-0000-0000-00000000a006";
        Dv.AddUser(former, AdminDataverse.AdminRoleId);
        Dv.Disable(former);
        await Assert.ThrowsAsync<CallerRejectedException>(() => Read(Inventory(), former));
    }

    [Fact]
    public async Task Admin_AsyncCall_Refused()
    {
        // M3: an async call is never an administrator's, whatever the registration allows.
        var request = Request("bf_ReadDefinition", Inventory());
        request.Caller!.Async = true;
        var ex = await Assert.ThrowsAsync<CallerRejectedException>(() => Api.HandleAsync(request, CancellationToken.None));
        Assert.Contains("async", ex.Message);
    }

    [Fact]
    public async Task Admin_UnknownUser_Refused() =>
        await Assert.ThrowsAsync<CallerRejectedException>(() => Read(Inventory(), "00000000-0000-0000-0000-00000000a0ff"));

    [Fact]
    public async Task Admin_RoleFromAnotherSolution_Refused()
    {
        const string impostor = "00000000-0000-0000-0000-00000000a003";
        Dv.AddUser(impostor, Dv.AddHandMadeAdminRole());
        await Assert.ThrowsAsync<CallerRejectedException>(() => Read(Inventory(), impostor));
    }

    [Fact]
    public async Task Admin_RoleThroughTeam_Allowed()
    {
        const string member = "00000000-0000-0000-0000-00000000a004";
        Dv.AddUser(member);
        Dv.AddTeamRole(member, AdminDataverse.AdminRoleId);
        Assert.NotNull((await Read(Inventory(), member))["review"]);
    }

    [Fact]
    public async Task Admin_ChildBusinessUnitCopy_Allowed()
    {
        // AddUser gives the user a copy of the role (its own role ID) whose root is the core role, as a child business unit has.
        const string child = "00000000-0000-0000-0000-00000000a005";
        Dv.AddUser(child, AdminDataverse.AdminRoleId);
        Assert.NotNull((await Read(Inventory(), child))["review"]);
    }

    [Fact]
    public async Task Read_InventoryReady_ReviewAndPreviewWithFingerprint()
    {
        var read = await Read(Inventory());
        Assert.Equal("Ready", (string?)read["status"]);
        Assert.NotNull(read["review"]!["result"]);
        var preview = read["preview"]!.AsObject();
        Assert.Matches("^[0-9a-f]{16}-[0-9a-f]{16}$", (string)preview["fingerprint"]!);
        Assert.Contains(preview["changes"]!.AsArray(), c => ((string)c!).Contains("ext_item"));
        Assert.Contains(preview["pending"]!.AsArray(), p => ((string)p!).Contains("InventoryAPI user"));
        Assert.Equal($"{PublicUrl}/admin/{Env}/InventoryAPI/secret", (string?)read["secretUrl"]);
        Assert.Equal($"{PublicUrl}/admin/{Env}/InventoryAPI/role", (string?)read["roleUrl"]);
        Assert.False((bool)read["readOnly"]!);
    }

    [Fact]
    public async Task Read_OpenQuestions_NoPreviewStatusNeedsAnswers()
    {
        var read = await Read(Inventory(answers: "{}"));
        Assert.Equal("NeedsAnswers", (string?)read["status"]);
        Assert.Null(read["preview"]);
        Assert.Contains(read["review"]!["endpoints"]!.AsArray(), e => (string?)e!["status"] == "NeedsAnswers");
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html><body>not a definition</body></html>")]
    public async Task Read_EmptyDefinition_ProblemNotError(string text)
    {
        var id = Dv.AddRow(DesignBuilderTests.Inventory() with { OpenApi = text });
        var read = await Read(id);
        Assert.Equal("Invalid", (string?)read["status"]);
        Assert.NotEmpty(read["review"]!["problems"]!.AsArray());
    }

    [Fact]
    public async Task Read_NoDefinitionOrTooLong_ProblemNotError()
    {
        // M5: a row with no OpenAPI text, and text over the draft service's limit.
        foreach (var text in new[] { null, new string('a', BrokerForge.SwaggerForge.DraftService.MaxDefinitionLength + 1) })
        {
            var id = Dv.AddRow(DesignBuilderTests.Inventory() with { Id = Guid.NewGuid(), Name = "Api" + Guid.NewGuid().ToString("N")[..6], OpenApi = text });
            var read = await Read(id);
            Assert.Equal("Invalid", (string?)read["status"]);
            Assert.NotEmpty(read["review"]!["problems"]!.AsArray());
        }
    }

    [Fact]
    public async Task Read_PreviewFails_CauseNamedInReview()
    {
        // M2: a Dataverse refusal while working out the change list is shown with its cause, not a generic 500.
        Dv.Forbid = "EntityDefinitions";
        var read = await Read(Inventory());
        Assert.Equal("Ready", (string?)read["status"]);
        Assert.Contains("SecLib::CheckPrivilege failed (test)", (string?)read["preview"]!["error"]);
        Assert.Null(read["preview"]!["fingerprint"]);
    }

    [Fact]
    public async Task Read_MissingRow_NotFound()
    {
        var ex = await Assert.ThrowsAsync<MappedSourceException>(() => Read(Guid.Parse("00000000-0000-0000-0000-0000000000ff")));
        Assert.Equal(SourceErrorKinds.NotFound, ex.Kind);
    }

    [Fact]
    public async Task Read_NoConnectionId_Refused()
    {
        var ex = await Assert.ThrowsAsync<MappedSourceException>(() => Api.HandleAsync(Request("bf_ReadDefinition", null), CancellationToken.None));
        Assert.Contains("ConnectionId", ex.Message);
    }

    [Fact]
    public async Task Read_EnvironmentStatus_NamesMissingKey()
    {
        var env = (await Read(Inventory()))["environment"]!;
        Assert.True((bool)env["baseUrlSet"]!);
        var key = Assert.Single(env["secrets"]!.AsArray())!;
        Assert.Equal("key", (string?)key["name"]);
        Assert.False((bool)key["set"]!);
    }

    [Fact]
    public async Task Read_NeverEchoesSecret()
    {
        const string value = "bf-test-dummy-inventory-key";
        await Secrets.SetAsync(Env, "InventoryAPI", "key", value, CancellationToken.None);
        var read = await Read(Inventory());
        Assert.True((bool)read["environment"]!["secrets"]![0]!["set"]!);
        Assert.DoesNotContain(value, read.ToJsonString());
    }
}
