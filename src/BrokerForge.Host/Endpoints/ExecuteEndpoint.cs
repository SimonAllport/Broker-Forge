using System.Diagnostics;
using System.Text.RegularExpressions;
using BrokerForge.Abstractions;
using BrokerForge.Contracts;
using BrokerForge.Descriptors.Mapping;
using BrokerForge.Descriptors.Model;
using BrokerForge.Descriptors.Planning;
using BrokerForge.Descriptors.Rendering;
using BrokerForge.Host.Admin;
using BrokerForge.Host.Config;
using BrokerForge.Host.Errors;
using BrokerForge.Host.Execution;
using BrokerForge.Host.Ingress;
using BrokerForge.Host.Routing;
using BrokerForge.Keys;

namespace BrokerForge.Host.Endpoints;

/// <summary>
/// POST /v1/execute: one canonical request in, one canonical response (or error payload) out (HLD 4).
/// Every log line runs in a correlationId scope; bodies and secret values are never logged.
/// </summary>
public static partial class ExecuteEndpoint
{
    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex CorrelationIdFormat();

    private static readonly HashSet<string> TableMessages = new() { Messages.Retrieve, Messages.RetrieveMultiple, Messages.Create, Messages.Update, Messages.Delete };

    public static void Map(WebApplication app) =>
        app.MapPost("/v1/execute", Handle).RequireAuthorization();

    private static async Task<IResult> Handle(
        CanonicalRequest request,
        HttpContext http,
        CallerGuard guard,
        RequestRouter router,
        IHostConfigProvider configs,
        ISecretResolver secrets,
        PageTokenKey tokenKey,
        IConfiguration configuration,
        ILoggerFactory loggers,
        AdminApi admin,
        CancellationToken ct)
    {
        var log = loggers.CreateLogger("BrokerForge.Execute");
        var correlationId = request.CorrelationId is { } id && CorrelationIdFormat().IsMatch(id) ? id : null;
        if (correlationId == null)
        {
            var generated = Guid.NewGuid().ToString();
            return Results.Json(new ErrorPayload { CorrelationId = generated, Error = "BadRequest", Message = "The request needs a correlationId of letters, digits and hyphens." }, statusCode: 400);
        }

        // The caller is what per-user credentials are keyed by (HLD D-27), so every line says who and whether async.
        using var scope = log.BeginScope(new Dictionary<string, object>
        {
            ["correlationId"] = correlationId,
            // Caller-supplied and not yet validated: only a GUID (in its canonical form) goes into the log scope.
            ["environmentId"] = Guid.TryParse(request.EnvironmentId, out var environment) ? environment.ToString("D") : "(none)",
            ["userObjectId"] = request.Caller?.UserObjectId ?? "(none)",
            ["async"] = request.Caller?.Async ?? false,
        });
        var watch = Stopwatch.StartNew();
        try
        {
            guard.Check(http.User, request);
            // The admin Custom APIs (spec 5, 6) answer from the connection rows, not a descriptor, and need no loaded config.
            if (AdminApi.Messages.Contains(request.Message ?? ""))
            {
                var answer = await admin.HandleAsync(request, ct);
                log.LogInformation("Completed admin message {Message} in {ElapsedMs} ms", request.Message, watch.ElapsedMilliseconds);
                return Results.Json(answer);
            }
            var config = await configs.ForEnvironmentAsync(request.EnvironmentId!, ct);
            var timeout = TimeSpan.FromSeconds(configuration.GetValue("Host:TimeoutSeconds", 90));
            if (PrimaryKeyLookup(request, router, config) is { } lookup)
            {
                var rows = await LookUpByKey(lookup, router, config, secrets, tokenKey, Deadline.After(timeout), ct);
                log.LogInformation("Completed {Message} on {Table} as a primary key lookup in {ElapsedMs} ms", request.Message, request.Table, watch.ElapsedMilliseconds);
                return Results.Json(new CanonicalResponse { CorrelationId = correlationId, Rows = rows });
            }
            if (!TableMessages.Contains(request.Message ?? ""))
            {
                var (api, apiBroker) = await router.ResolveCustomApiAsync(config, request, ct);
                log.LogInformation("Executing Custom API {Message} with {Operation} v{Version}{Design}", request.Message, api.Descriptor.Operation, api.Descriptor.Version, DesignLabel(api.Instance));
                var result = await Call(api.Descriptor, api.Binding, api.Channel, new OperationExecutor(apiBroker), secrets, Deadline.After(timeout), ct);
                var outputs = ResponseMapper.MapOutputs(api.Descriptor, result, correlationId);
                log.LogInformation("Completed Custom API {Message} in {ElapsedMs} ms", request.Message, watch.ElapsedMilliseconds);
                return Results.Json(new CanonicalResponse { CorrelationId = correlationId, Outputs = outputs });
            }
            var (op, broker) = await router.ResolveAsync(config, request, ct);
            log.LogInformation("Executing {Message} on {Table} with {Operation} v{Version}{Design}", request.Message, request.Table, op.Descriptor.Operation, op.Descriptor.Version, DesignLabel(op.Instance));

            var response = await Run(request, op, new OperationExecutor(broker), secrets, tokenKey, Deadline.After(timeout), ct);
            response.CorrelationId = correlationId;
            log.LogInformation("Completed {Message} on {Table} in {ElapsedMs} ms", request.Message, request.Table, watch.ElapsedMilliseconds);
            return Results.Json(response);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var (status, payload) = ErrorTranslator.Translate(ex, correlationId);
            if (status >= 500 && payload.Error is "InternalError" or "ConfigurationError")
                log.LogError(ex, "Failed {Message} on {Table} after {ElapsedMs} ms with {Status}", request.Message, request.Table, watch.ElapsedMilliseconds, status);
            else
                log.LogWarning("Failed {Message} on {Table} after {ElapsedMs} ms with {Status} {Error}: {Reason}", request.Message, request.Table, watch.ElapsedMilliseconds, status, payload.Error, payload.Message);
            return Results.Json(payload, statusCode: status);
        }
    }

    /// <summary>" (design v7)" for an instance built from a connection design, so each call names the approved version it ran (spec 6).</summary>
    private static string DesignLabel(ServiceInstance instance) => instance.DesignVersion is { } v ? $" (design v{v})" : "";

    /// <summary>
    /// A RetrieveMultiple whose only condition is primary key eq one GUID, on a table with a Retrieve
    /// operation. The platform sends these for Web API upserts and record lookups (phase 1 findings); the
    /// source answers them through the Retrieve descriptor, so nothing is filtered in the host.
    /// </summary>
    private static CanonicalRequest? PrimaryKeyLookup(CanonicalRequest request, RequestRouter router, HostConfigStore config)
    {
        if (request.Message != Messages.RetrieveMultiple || !router.HasOperation(config, request.Table, Messages.Retrieve))
            return null;
        var filter = request.Query?.Filter;
        if (filter is not { Type: "and", Conditions.Count: 1 } || filter.Groups is { Count: > 0 } || request.Query!.Page?.Token != null)
            return null;
        var c = filter.Conditions[0];
        if (c.Col != request.Table + "id" || c.Op != Ops.Eq || c.Values is not { Count: 1 } || !Guid.TryParse(c.Values[0], out _))
            return null;
        return new CanonicalRequest
        {
            CorrelationId = request.CorrelationId,
            EnvironmentId = request.EnvironmentId,
            ServiceInstance = request.ServiceInstance,
            Table = request.Table,
            Message = Messages.Retrieve,
            Caller = request.Caller,
            Id = c.Values[0],
        };
    }

    private static async Task<List<Row>> LookUpByKey(CanonicalRequest retrieve, RequestRouter router, HostConfigStore config, ISecretResolver secrets, PageTokenKey tokenKey, Deadline deadline, CancellationToken ct)
    {
        try
        {
            var (op, broker) = await router.ResolveAsync(config, retrieve, ct);
            var response = await Run(retrieve, op, new OperationExecutor(broker), secrets, tokenKey, deadline, ct);
            return new List<Row> { response.Record };
        }
        catch (Exception ex) when (ex is InvalidKeyException || ex is MappedSourceException { Kind: SourceErrorKinds.NotFound })
        {
            return new List<Row>();
        }
    }

    private static async Task<CanonicalResponse> Run(CanonicalRequest request, OperationContext op, OperationExecutor executor, ISecretResolver secrets, PageTokenKey tokenKey, Deadline deadline, CancellationToken ct)
    {
        var d = op.Descriptor;
        var correlationId = op.Binding.CorrelationId;
        switch (request.Message)
        {
            case Messages.RetrieveMultiple:
                var plan = QueryPlanner.Plan(d, request.Query ?? new CanonicalQuery(), correlationId);
                var (rows, next) = await new PagingCoordinator(executor, secrets, tokenKey.Value).ReadPageAsync(op, plan, deadline, ct);
                return new CanonicalResponse { Rows = rows.Select(r => ToRow(op, r)).ToList(), Next = next };

            case Messages.Retrieve:
                var result = await Call(op, executor, secrets, deadline, ct);
                var row = ResponseMapper.MapRows(d, result, op.Keys.Kind, correlationId).Single();
                if (row.Key != op.Binding.Id)
                    throw new MappingException($"The source returned record {row.Key.Value} when asked for {op.Binding.Id!.Value} (operation {d.Operation}).", correlationId);
                return new CanonicalResponse { Record = new Row { Id = request.Id, Cells = row.Cells.ToList() } };

            case Messages.Create:
                RefuseUnboundColumns(request, op);
                var created = await Call(op, executor, secrets, deadline, ct);
                return new CanonicalResponse { Id = Encode(op, ResponseMapper.MapCreatedKey(d, created, op.Keys.Kind, correlationId)) };

            default:
                if (request.Message == Messages.Update)
                    RefuseUnboundColumns(request, op);
                await Call(op, executor, secrets, deadline, ct);
                return new CanonicalResponse();
        }
    }

    /// <summary>
    /// A supplied value no binding sends would be dropped while Dataverse reports success, so refuse it.
    /// A null on Create is tolerated: it asks for nothing.
    /// </summary>
    private static void RefuseUnboundColumns(CanonicalRequest request, OperationContext op)
    {
        var bound = RequestRenderer.BoundColumns(op.Descriptor.Request);
        var dropped = (request.Record ?? new List<Cell>())
            .Where(c => !bound.Contains(c.Col) && (request.Message == Messages.Update || c.Value != null))
            .Select(c => c.Col)
            .ToList();
        if (dropped.Count > 0)
            throw new MappedSourceException(SourceErrorKinds.Validation,
                $"Column(s) {string.Join(", ", dropped)} cannot be saved: operation {op.Descriptor.Operation} does not send them to the source.",
                op.Binding.CorrelationId);
    }

    private static Task<BrokerResult> Call(OperationContext op, OperationExecutor executor, ISecretResolver secrets, Deadline deadline, CancellationToken ct) =>
        Call(op.Descriptor, op.Binding, op.Channel, executor, secrets, deadline, ct);

    private static async Task<BrokerResult> Call(OperationDescriptor d, BindingContext binding, IAuthenticatedChannel channel, OperationExecutor executor, ISecretResolver secrets, Deadline deadline, CancellationToken ct)
    {
        var request = await RequestRenderer.RenderAsync(d.Request, d, binding, secrets, ct);
        var result = await executor.ExecuteAsync(d, request, channel, deadline, binding.CorrelationId, ct);
        ErrorMapper.ThrowIfError(d, result, binding.CorrelationId, PagingCoordinator.Redact(request, channel));
        return result;
    }

    private static Row ToRow(OperationContext op, MappedRow row) => new() { Id = Encode(op, row.Key), Cells = row.Cells.ToList() };

    private static string Encode(OperationContext op, ExternalKey key)
    {
        try
        {
            return op.Keys.Encode(key).ToString();
        }
        catch (KeyTooLongException ex)
        {
            throw new MappingException($"Source key {key.Value} does not fit the {op.Map.KeyStrategy} key strategy: {ex.Message} (table {op.Map.Table}).", op.Binding.CorrelationId, ex);
        }
    }
}
