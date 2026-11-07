using System.Net;
using Azure;
using Azure.Core;
using Azure.Data.Tables;
using BrokerForge.Abstractions;

namespace BrokerForge.Host.Generation;

/// <summary>
/// One Generate per environment and instance across every host process (HLD D-36): replicas and the admin command share
/// it. A lease lasts a fixed time, so one left by a process that stopped is taken over once it has expired.
/// </summary>
public interface IGenerationLease
{
    /// <summary>A holder token, or null while another holder's lease has not expired.</summary>
    Task<string?> TryAcquireAsync(string environmentId, string instance, TimeSpan duration, CancellationToken ct);

    /// <summary>Frees the lease if <paramref name="token"/> still holds it; otherwise does nothing.</summary>
    Task ReleaseAsync(string environmentId, string instance, string token, CancellationToken ct);
}

public static class GenerationLeases
{
    public const string Partition = "generation-lease";

    /// <summary>Dataverse names are case-insensitive, so WarehouseAPI and warehouseapi are one source.</summary>
    public static string Key(string environmentId, string instance) => $"{environmentId.ToLowerInvariant()}|{instance.ToLowerInvariant()}";

    /// <summary>
    /// The credential table when <c>Credentials:TableEndpoint</c> is set (production). Without it, in memory: a host with
    /// connection designs may run several replicas, so it then refuses to start unless <c>Credentials:InMemory</c> asks
    /// for that, as the credential stores do.
    /// </summary>
    public static IGenerationLease Create(IConfiguration configuration, TokenCredential credential, TimeProvider time, bool designs)
    {
        var endpoint = configuration["Credentials:TableEndpoint"];
        if (string.IsNullOrWhiteSpace(endpoint))
            return !designs || configuration.GetValue("Credentials:InMemory", false)
                ? new InMemoryGenerationLease(time)
                : throw new ConfigurationException("Credentials:TableEndpoint is required: connection designs are generated under a lock that every host replica must share.");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            throw new ConfigurationException("Credentials:TableEndpoint must be the storage account's https table endpoint.");
        return new TableGenerationLease(new TableClient(uri, configuration["Credentials:TableName"] ?? Config.CredentialStores.DefaultTable, credential), time);
    }
}

/// <summary>
/// A lease row in the credential table: PartitionKey <c>generation-lease</c>, RowKey <c>&lt;environment&gt;|&lt;instance&gt;</c>,
/// with the holder token and expiry. Taken by insert, or by an ETag-conditional replace of an expired row, so two
/// processes racing for it cannot both win.
/// </summary>
public sealed class TableGenerationLease(TableClient table, TimeProvider time) : IGenerationLease
{
    public async Task<string?> TryAcquireAsync(string environmentId, string instance, TimeSpan duration, CancellationToken ct)
    {
        var token = Guid.NewGuid().ToString("N");
        var entity = new TableEntity(GenerationLeases.Partition, GenerationLeases.Key(environmentId, instance))
        {
            ["Holder"] = token,
            ["Expires"] = time.GetUtcNow() + duration,
        };
        try
        {
            await table.AddEntityAsync(entity, ct);
            return token;
        }
        catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.Conflict)
        {
        }
        var current = await table.GetEntityIfExistsAsync<TableEntity>(entity.PartitionKey, entity.RowKey, cancellationToken: ct);
        if (!current.HasValue)
            return null;
        if (current.Value!.GetDateTimeOffset("Expires") is { } expires && expires > time.GetUtcNow())
            return null;
        try
        {
            await table.UpdateEntityAsync(entity, current.Value.ETag, TableUpdateMode.Replace, ct);
            return token;
        }
        catch (RequestFailedException ex) when (ex.Status is (int)HttpStatusCode.PreconditionFailed or (int)HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task ReleaseAsync(string environmentId, string instance, string token, CancellationToken ct)
    {
        var key = GenerationLeases.Key(environmentId, instance);
        var current = await table.GetEntityIfExistsAsync<TableEntity>(GenerationLeases.Partition, key, cancellationToken: ct);
        if (!current.HasValue || current.Value!.GetString("Holder") != token)
            return;
        try
        {
            await table.DeleteEntityAsync(GenerationLeases.Partition, key, current.Value.ETag, ct);
        }
        catch (RequestFailedException ex) when (ex.Status is (int)HttpStatusCode.PreconditionFailed or (int)HttpStatusCode.NotFound)
        {
            // Taken over or already gone: not this holder's to free.
        }
    }
}

/// <summary>In one process only: for tests and local runs without the credential table.</summary>
public sealed class InMemoryGenerationLease(TimeProvider time) : IGenerationLease
{
    private readonly Dictionary<string, (string Token, DateTimeOffset Expires)> _leases = new();

    public Task<string?> TryAcquireAsync(string environmentId, string instance, TimeSpan duration, CancellationToken ct)
    {
        var key = GenerationLeases.Key(environmentId, instance);
        lock (_leases)
        {
            if (_leases.TryGetValue(key, out var held) && held.Expires > time.GetUtcNow())
                return Task.FromResult<string?>(null);
            var token = Guid.NewGuid().ToString("N");
            _leases[key] = (token, time.GetUtcNow() + duration);
            return Task.FromResult<string?>(token);
        }
    }

    public Task ReleaseAsync(string environmentId, string instance, string token, CancellationToken ct)
    {
        var key = GenerationLeases.Key(environmentId, instance);
        lock (_leases)
            if (_leases.TryGetValue(key, out var held) && held.Token == token)
                _leases.Remove(key);
        return Task.CompletedTask;
    }
}
