using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BrokerForge.Auth.Credentials;
using BrokerForge.Contracts;
using BrokerForge.Generator;
using BrokerForge.Host.Config;
using BrokerForge.Host.Generation;
using BrokerForge.Host.Ingress;
using BrokerForge.SwaggerForge;
using BrokerForge.Schema;
using BrokerForge.Descriptors.Parsing;

namespace BrokerForge.Host.Admin;

/// <summary>
/// The admin Custom APIs (spec 5, 6): unbound messages served by the same <c>CustomApiPlugin</c> as a source's actions, but
/// answered here rather than by a descriptor. Every call first checks that the caller is a BrokerForge administrator,
/// reading Dataverse as the host's own application user. Nothing here returns or logs a secret value.
/// </summary>
public sealed class AdminApi(IGenerationClientFactory clients, GenerationJobs jobs, IInstanceSecretStore secrets, IConfiguration configuration,
    IHostConfigProvider? configs = null)
{
    public const string ReadDefinition = "bf_ReadDefinition";
    public const string Generate = "bf_Generate";
    public const string ListConnections = "bf_ListConnections";

    public static readonly IReadOnlySet<string> Messages = new HashSet<string>(StringComparer.Ordinal) { ReadDefinition, Generate, ListConnections };

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public async Task<CanonicalResponse> HandleAsync(CanonicalRequest request, CancellationToken ct)
    {
        var correlationId = request.CorrelationId!;
        // An async call runs as nobody who clicked anything: never an administrator's, whatever the registration allows.
        if (request.Caller?.Async == true)
            throw new CallerRejectedException("BrokerForge admin messages cannot run async: " + AdminCheck.Refusal);
        var host = clients.Create(jobs.OrgUrlOf(request.EnvironmentId!));
        await AdminCheck.RequireAsync(host, request.Caller?.UserObjectId, correlationId, ct);
        var outputs = request.Message switch
        {
            ReadDefinition => new List<Cell> { Text("Review", (await ReadAsync(host, request, ConnectionId(request), ct)).ToJsonString()) },
            Generate => await GenerateAsync(host, request, ConnectionId(request), ct),
            ListConnections => new List<Cell> { Text("Connections", (await ListAsync(host, request.EnvironmentId!, ct)).ToJsonString()) },
            _ => throw new UnsupportedQueryException($"Message {request.Message} is not a BrokerForge admin message.", correlationId),
        };
        return new CanonicalResponse { CorrelationId = correlationId, Outputs = outputs };
    }

    /// <summary>
    /// The review (spec 5, screen 2): the draft service's answer for the row, the change list Generate would apply (only
    /// when nothing is open), this environment's URL and key status, and the host pages for keys and the role.
    /// </summary>
    private async Task<JsonObject> ReadAsync(DataverseClient host, CanonicalRequest request, Guid id, CancellationToken ct)
    {
        var correlationId = request.CorrelationId!;
        var env = request.EnvironmentId!;
        var row = await ConnectionRows.ReadAsync(host, id, correlationId, ct);
        var review = DesignBuilder.Review(row, correlationId);
        JsonNode? preview = null;
        if (review.Ready)
        {
            var design = DesignBuilder.Build(row, review, correlationId);
            try
            {
                var p = await jobs.PreviewAsync(new GenerationRequest(env, design, DesignBuilder.SolutionOf(row), DesignBuilder.PublisherOf(row), row.BaseUrl, null, null, correlationId), ct);
                preview = new JsonObject
                {
                    // The change list's fingerprint and the design's, so an answer that changes only a descriptor is caught too.
                    ["fingerprint"] = $"{p.Changes.Fingerprint}-{DesignFingerprint(design)}",
                    ["changes"] = Strings(p.Changes.Changes.Select(c => c.Text)),
                    ["refused"] = Strings(p.Changes.Refusals),
                    ["blocked"] = Strings(p.Blocked.Select(b => $"{b.Change.Text} is blocked: {b.Dependent} uses it")),
                    ["parts"] = Strings(p.Parts),
                    ["pending"] = Strings(p.Pending),
                    ["needsConfirmation"] = p.NeedsConfirmation,
                };
            }
            catch (Exception ex) when (ex is GenerationRefusedException or DataverseException or SchemaException or DescriptorException or BrokerForge.Abstractions.ConfigurationException)
            {
                // The review is fine; working out what Generate would change was not. Say why, rather than a bare 500.
                preview = new JsonObject { ["error"] = ex.Message };
            }
        }
        var secretNames = DesignBuilder.Auth(row).SecretNames;
        var keys = new JsonArray();
        foreach (var name in secretNames)
            keys.Add(new JsonObject { ["name"] = name, ["set"] = await secrets.ExistsAsync(env, row.Name, name, ct) });
        return new JsonObject
        {
            ["status"] = Status(review),
            ["review"] = JsonSerializer.SerializeToNode(review, Json),
            ["preview"] = preview,
            ["environment"] = new JsonObject { ["baseUrlSet"] = row.BaseUrl != null, ["secrets"] = keys },
            ["secretUrl"] = PageUrl(env, row.Name, "secret"),
            ["roleUrl"] = PageUrl(env, row.Name, "role"),
            ["readOnly"] = false,
        };
    }

    /// <summary>
    /// Starts Generate for the row and returns at once (spec 6 step 3); progress goes to the row. The review is computed
    /// again here, so a row changed since the admin looked at it gives a different change list, which the confirmed
    /// fingerprint then refuses. As the host, the role is left to an administrator (D-36).
    /// </summary>
    private async Task<List<Cell>> GenerateAsync(DataverseClient host, CanonicalRequest request, Guid id, CancellationToken ct)
    {
        var correlationId = request.CorrelationId!;
        var row = await ConnectionRows.ReadAsync(host, id, correlationId, ct);
        await ConnectionList.RowAsync(host, row.Name, ct, correlationId);
        var design = DesignBuilder.Build(row, DesignBuilder.Review(row, correlationId), correlationId);
        var fingerprint = request.Inputs?.FirstOrDefault(c => c.Col == "Fingerprint")?.Value is { Length: > 0 } f ? f : null;
        string? changes = null;
        if (fingerprint != null)
        {
            var parts = fingerprint.Split('-');
            if (parts.Length != 2)
                throw new MappedSourceException(SourceErrorKinds.Validation, $"Fingerprint {fingerprint} is not one bf_ReadDefinition returned.", correlationId);
            if (parts[1] != DesignFingerprint(design))
                throw new MappedSourceException(SourceErrorKinds.Conflict,
                    $"The connection's answers or definition changed since the review ({fingerprint}); nothing was generated. Read the definition again and confirm the new list.", correlationId);
            changes = parts[0];
        }
        GenerationJob job;
        try
        {
            job = await jobs.StartAsync(new GenerationRequest(request.EnvironmentId!, design, DesignBuilder.SolutionOf(row), DesignBuilder.PublisherOf(row), row.BaseUrl, null,
                changes, correlationId, ApplyRole: false, ConnectionId: id), ct);
        }
        catch (GenerationBusyException ex)
        {
            throw new MappedSourceException(SourceErrorKinds.Conflict, ex.Message, correlationId);
        }
        return new List<Cell> { Text("JobId", job.Id.ToString("D")), Text("State", job.State.ToString()) };
    }

    /// <summary>This environment's rows and imported designs with their status (spec 9). A design that failed to load is in the config's failures, not here.</summary>
    private async Task<JsonArray> ListAsync(DataverseClient host, string env, CancellationToken ct)
    {
        var config = await (configs ?? throw new InvalidOperationException("AdminApi has no config provider.")).ForEnvironmentAsync(env, ct);
        return await ConnectionList.ListAsync(host, config, env, secrets, instance => PageUrl(env, instance, "secret"), ct);
    }

    /// <summary>The first 16 hex characters of SHA-256 over the serialised design (version left out, as publishing sets it).</summary>
    internal static string DesignFingerprint(BrokerForge.Connections.ConnectionDesign design) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            BrokerForge.Connections.DesignSerializer.Serialize(design with { Version = 0 }))))[..16].ToLowerInvariant();

    /// <summary>Ready; Invalid when the definition or an endpoint has problems; otherwise NeedsAnswers.</summary>
    internal static string Status(DraftReview review) =>
        review.Ready ? "Ready"
        : review.Problems.Count > 0 || review.Endpoints.Any(e => e.Status == EndpointStatus.Invalid) ? "Invalid"
        : "NeedsAnswers";

    /// <summary>The host's admin page for this connection, or null when Connect:PublicBaseUrl is not set.</summary>
    internal string? PageUrl(string env, string instance, string page) =>
        configuration["Connect:PublicBaseUrl"] is { Length: > 0 } baseUrl
            ? $"{baseUrl.TrimEnd('/')}/admin/{Guid.Parse(env):D}/{Uri.EscapeDataString(instance)}/{page}"
            : null;

    internal static Guid ConnectionId(CanonicalRequest request) =>
        request.Inputs?.FirstOrDefault(c => c.Col == "ConnectionId")?.Value is { } v && Guid.TryParse(v, out var id)
            ? id
            : throw new MappedSourceException(SourceErrorKinds.Validation, $"{request.Message} needs the ConnectionId of a connection.", request.CorrelationId!);

    internal static Cell Text(string name, string value) => new() { Col = name, Type = CellTypes.String, Value = value };

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
}
