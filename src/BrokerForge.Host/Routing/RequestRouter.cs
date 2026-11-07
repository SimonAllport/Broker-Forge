using System.Globalization;
using BrokerForge.Abstractions;
using BrokerForge.Contracts;
using BrokerForge.Descriptors.Rendering;
using BrokerForge.Host.Config;
using BrokerForge.Host.Execution;
using BrokerForge.Keys;

namespace BrokerForge.Host.Routing;

/// <summary>
/// Resolves a canonical request to its object map, approved descriptor, key strategy and authenticated
/// channel (HLD 4 step 3, 5), in the configuration of the request's environment. A table, message or instance that
/// does not line up is refused.
/// </summary>
public sealed class RequestRouter(IEnumerable<IAuthProvider> providers, IEnumerable<IBroker> brokers)
{
    private readonly Dictionary<AuthMode, IAuthProvider> _providers = providers.ToDictionary(p => p.Mode);
    private readonly Dictionary<string, IBroker> _brokers = brokers.ToDictionary(b => b.BrokerType);

    public bool HasOperation(HostConfigStore config, string? table, string message) =>
        table != null && config.Map(table)?.Map.Operations.ContainsKey(message) == true;

    public async Task<(OperationContext Op, IBroker Broker)> ResolveAsync(HostConfigStore config, CanonicalRequest request, CancellationToken ct)
    {
        var correlationId = request.CorrelationId!;
        UnsupportedQueryException Reject(string why) => new(why, correlationId);

        // A design left out of this environment may be what maps the table: say why, not "not mapped".
        var resolved = config.Map(request.Table ?? "")
            ?? throw (Exception?)config.NotLoaded($"Table {request.Table}") ?? Reject($"Table {request.Table} is not mapped to a BrokerForge source.");
        if (resolved.Map.ServiceInstance != request.ServiceInstance && config.Instance(request.ServiceInstance ?? "") == null
            && config.NotLoaded($"Service instance {request.ServiceInstance}") is { } notLoaded)
            throw notLoaded;
        if (resolved.Map.ServiceInstance != request.ServiceInstance)
            throw Reject($"Table {request.Table} belongs to service instance {resolved.Map.ServiceInstance}, not {request.ServiceInstance}.");
        if (!resolved.Map.Operations.TryGetValue(request.Message ?? "", out var operation))
            throw Reject($"Table {request.Table} has no {request.Message} operation.");
        var instance = config.Instance(resolved.Map.ServiceInstance)!;
        var descriptor = config.Descriptor(instance.Name, operation)!;

        var (broker, channel) = await Connect(instance, request, correlationId, ct);
        var caller = request.Caller ?? new CallerContext();

        var record = new Dictionary<string, Cell>(StringComparer.Ordinal);
        foreach (var cell in request.Record ?? new List<Cell>())
            if (cell.Col == null || !record.TryAdd(cell.Col, cell))
                throw new MappedSourceException(SourceErrorKinds.Validation, $"The record names column {cell.Col ?? "(none)"} twice or not at all.", correlationId);

        var binding = new BindingContext(
            instance,
            correlationId,
            request.EnvironmentId!,
            caller.UserObjectId,
            caller.Async,
            record,
            request.Message == Messages.Update ? record.Keys.ToHashSet(StringComparer.Ordinal) : null,
            DecodeId(request, resolved.Keys),
            null,
            null);
        return (new OperationContext(instance, resolved.Map, descriptor, resolved.Keys, channel, binding), broker);
    }

    /// <summary>
    /// An unbound Custom API (HLD D-18): the message is its unique name and the descriptor that binds it names
    /// the instance. Inputs are checked against the schema's declared parameters before anything is sent.
    /// </summary>
    public async Task<(CustomApiContext Api, IBroker Broker)> ResolveCustomApiAsync(HostConfigStore config, CanonicalRequest request, CancellationToken ct)
    {
        var correlationId = request.CorrelationId!;
        var name = request.Message ?? "";
        var api = config.CustomApi(name)
            ?? throw (Exception?)config.NotLoaded(name.Length == 0 ? "A request without a message" : $"Message {name}")
            ?? new UnsupportedQueryException($"{(name.Length == 0 ? "The request names no message" : $"Message {name} is not a virtual table message or a BrokerForge Custom API")}.", correlationId);
        if (request.Table != null)
            throw new UnsupportedQueryException($"Custom API {name} is unbound; the request must not name table {request.Table}.", correlationId);
        if (request.ServiceInstance != null && request.ServiceInstance != api.Instance.Name)
            throw new UnsupportedQueryException($"Custom API {name} belongs to service instance {api.Instance.Name}, not {request.ServiceInstance}.", correlationId);

        var inputs = CheckInputs(name, api.Schema, request.Inputs ?? new List<Cell>(), correlationId);
        var (broker, channel) = await Connect(api.Instance, request, correlationId, ct);
        var caller = request.Caller ?? new CallerContext();
        var binding = new BindingContext(api.Instance, correlationId, request.EnvironmentId!, caller.UserObjectId, caller.Async,
            null, null, null, null, null, inputs);
        return (new CustomApiContext(api.Instance, api.Descriptor, channel, binding), broker);
    }

    private static Dictionary<string, Cell> CheckInputs(string api, Schema.CustomApiSchema schema, List<Cell> cells, string correlationId)
    {
        MappedSourceException Invalid(string message) => new(SourceErrorKinds.Validation, message, correlationId);
        var inputs = new Dictionary<string, Cell>(StringComparer.Ordinal);
        foreach (var cell in cells)
        {
            if (cell.Col == null || !inputs.TryAdd(cell.Col, cell))
                throw Invalid($"The inputs for {api} name parameter {cell.Col ?? "(none)"} twice or not at all.");
            if (!schema.Inputs.TryGetValue(cell.Col, out var declared))
                throw Invalid($"Custom API {api} has no input {cell.Col}.");
            if (cell.Value != null && (cell.Type != declared.Type || !Parses(declared.Type, cell.Value)))
                // The value is not echoed: an input may be sensitive, and this message reaches logs (design principle 4).
                throw Invalid($"Input {cell.Col} of {api} must be {declared.Type}; got {(cell.Type == declared.Type ? "a value that is not a valid " + declared.Type : cell.Type ?? "no type")}.");
        }
        foreach (var (param, declared) in schema.Inputs)
            if (declared.Required && inputs.GetValueOrDefault(param)?.Value == null)
                throw Invalid($"Custom API {api} needs input {param}.");
        return inputs;
    }

    private static bool Parses(string type, string value)
    {
        var inv = CultureInfo.InvariantCulture;
        return type switch
        {
            CellTypes.String => true,
            CellTypes.Int => int.TryParse(value, NumberStyles.AllowLeadingSign, inv, out _),
            CellTypes.Decimal => decimal.TryParse(value, NumberStyles.Number, inv, out _),
            CellTypes.Bool => value is "true" or "false",
            CellTypes.DateTime => DateTimeOffset.TryParse(value, inv, DateTimeStyles.AssumeUniversal, out _),
            CellTypes.Guid => Guid.TryParse(value, out _),
            _ => false,
        };
    }

    private async Task<(IBroker Broker, IAuthenticatedChannel Channel)> Connect(ServiceInstance instance, CanonicalRequest request, string correlationId, CancellationToken ct)
    {
        if (instance.Incomplete.Count > 0)
            throw new ConfigurationException($"Service instance {instance.Name} needs its {string.Join("; ", instance.Incomplete.Select(Missing))}.");
        var broker = _brokers.GetValueOrDefault(instance.BrokerType)
            ?? throw new ConfigurationException($"No adapter is registered for broker type {instance.BrokerType}.");
        var caller = request.Caller ?? new CallerContext();
        var effective = AuthSelector.Select(instance, caller);
        var provider = _providers.GetValueOrDefault(effective.Auth.Mode)
            ?? throw new ConfigurationException($"No authentication provider is registered for mode {effective.Auth.Mode}.");
        return (broker, await provider.AuthenticateAsync(effective, caller, correlationId, ct));
    }

    /// <summary>"base URL (environment variable bf_X_BaseUrl)" reads "base URL in this environment: set environment variable bf_X_BaseUrl".</summary>
    private static string Missing(string incomplete)
    {
        var open = incomplete.IndexOf(" (", StringComparison.Ordinal);
        return open < 0 || !incomplete.EndsWith(')')
            ? incomplete + " in this environment"
            : $"{incomplete[..open]} in this environment: set {incomplete[(open + 2)..^1]}";
    }

    private static ExternalKey? DecodeId(CanonicalRequest request, IKeyStrategy keys)
    {
        if (request.Message is not (Messages.Retrieve or Messages.Update or Messages.Delete))
            return null;
        if (!Guid.TryParse(request.Id, out var id))
            throw new MappedSourceException(SourceErrorKinds.Validation, $"{request.Message} needs the record id as a GUID.", request.CorrelationId!);
        return keys.Decode(id);
    }
}
