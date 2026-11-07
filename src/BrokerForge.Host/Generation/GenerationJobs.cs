using System.Collections.Concurrent;
using BrokerForge.Abstractions;
using BrokerForge.Connections;
using BrokerForge.Descriptors.Parsing;
using BrokerForge.Descriptors.Validation;
using BrokerForge.Generator;
using BrokerForge.Host.Config;
using BrokerForge.Host.Ingress;
using BrokerForge.Schema;
using Microsoft.Extensions.Options;

namespace BrokerForge.Host.Generation;

/// <summary>
/// Runs Generate in the background as the host's own application user (spec 6 step 3): a plug-in call has two
/// minutes, a generation can take longer. One job at a time per environment and instance; each has a time limit.
/// A job that succeeds reloads that environment's connections. Finished jobs are kept for a day in memory. Across processes
/// (replicas, the admin command) a lease (<see cref="IGenerationLease"/>) keeps one job per environment and instance; it
/// lasts the time limit plus 5 minutes, so a process that stops mid-job frees the source within that time.
/// </summary>
public sealed class GenerationJobs(IOptions<TrustOptions> trust, IGenerationClientFactory clients, IHostConfigProvider configs, IGenerationProgress progress,
    ILogger<GenerationJobs> log, TimeSpan? timeLimit = null, IGenerationLease? lease = null)
{
    private static readonly TimeSpan LeaseMargin = TimeSpan.FromMinutes(5);
    private readonly IGenerationLease _lease = lease ?? new InMemoryGenerationLease(TimeProvider.System);
    public static readonly TimeSpan DefaultTimeLimit = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Keep = TimeSpan.FromDays(1);

    private readonly TimeSpan _limit = timeLimit ?? DefaultTimeLimit;
    private readonly ConcurrentDictionary<Guid, GenerationJob> _jobs = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<GenerationJob>> _done = new();
    private readonly Dictionary<(string, string), Guid> _active = new();

    /// <summary>Queues the job and returns at once.</summary>
    /// <exception cref="ConfigurationException">The environment is not one the host serves, or has no OrgUrl.</exception>
    /// <exception cref="GenerationBusyException">A job for this environment and instance is still queued or running, here or in another host.</exception>
    public async Task<GenerationJob> StartAsync(GenerationRequest r, CancellationToken ct = default)
    {
        var org = OrgUrlOf(r.EnvironmentId);
        // Dataverse names are case-insensitive, so WarehouseAPI and warehouseapi are one source.
        var key = (r.EnvironmentId.ToLowerInvariant(), r.Design.Instance.ToLowerInvariant());
        GenerationJob job;
        lock (_active)
            if (_active.TryGetValue(key, out var running))
                throw new GenerationBusyException($"Generation of {r.Design.Instance} in environment {r.EnvironmentId} is already running (job {running}); wait for it to finish.");
        string? token;
        try
        {
            // Not the caller's token: a lease taken just as the caller gives up would hold the source with no job for 20 minutes.
            token = await _lease.TryAcquireAsync(r.EnvironmentId, r.Design.Instance, _limit + LeaseMargin, CancellationToken.None);
        }
        catch (Azure.RequestFailedException ex)
        {
            throw new GenerationBusyException($"The generation lock could not be taken (storage answered HTTP {ex.Status}); nothing was generated. Try again.");
        }
        if (token == null)
            throw new GenerationBusyException($"Generation of {r.Design.Instance} in environment {r.EnvironmentId} is already running in another host; wait for it to finish.");
        lock (_active)
        {
            if (_active.TryGetValue(key, out var running))
            {
                _ = _lease.ReleaseAsync(r.EnvironmentId, r.Design.Instance, token, CancellationToken.None);
                throw new GenerationBusyException($"Generation of {r.Design.Instance} in environment {r.EnvironmentId} is already running (job {running}); wait for it to finish.");
            }
            Forget();
            job = new GenerationJob(Guid.NewGuid(), r.EnvironmentId, r.Design.Instance, JobState.Queued, Array.Empty<string>(), null, null, DateTimeOffset.UtcNow, null, r.CorrelationId,
                ConnectionId: r.ConnectionId);
            _jobs[job.Id] = job;
            _done[job.Id] = new TaskCompletionSource<GenerationJob>(TaskCreationOptions.RunContinuationsAsynchronously);
            _active[key] = job.Id;
        }
        _ = Task.Run(() => RunAsync(job, r, org, key, token));
        return job;
    }

    public GenerationJob? Get(Guid id) => _jobs.GetValueOrDefault(id);

    /// <summary>The job once it has finished, succeeded or failed.</summary>
    public Task<GenerationJob> WaitAsync(Guid id) =>
        _done.TryGetValue(id, out var done) ? done.Task : throw new KeyNotFoundException($"no generation job {id}");

    /// <summary>The design published in the environment for <paramref name="instance"/>, read as the host (for regenerating it).</summary>
    public async Task<ConnectionDesign> ReadPublishedAsync(string environmentId, string instance, CancellationToken ct)
    {
        var client = clients.Create(OrgUrlOf(environmentId));
        var suffix = $"_/brokerforge/{instance}/design.xml";
        var found = (await client.ListAsync(DataverseDesignSource.DesignQuery, ct))
            .Where(w => ((string?)w["name"])?.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) == true).ToList();
        if (found.Count != 1)
            throw new ConfigurationException(found.Count == 0
                ? $"Environment {environmentId} has no published design for {instance}."
                : $"Environment {environmentId} has {found.Count} published designs for {instance}.");
        var name = (string)found[0]["name"]!;
        var xml = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String((string)found[0]["content"]!));
        return DesignSerializer.Parse(DesignCarrier.Unwrap(xml, name), name);
    }

    /// <summary>What Generate would change, for the admin to confirm. Reads only.</summary>
    public async Task<GenerationPreview> PreviewAsync(GenerationRequest r, CancellationToken ct)
    {
        var client = clients.Create(OrgUrlOf(r.EnvironmentId));
        var (schema, options) = await OptionsAsync(client, r, ct);
        return await Generator.Generator.PreviewAsync(client, schema, options, ct);
    }

    /// <summary>
    /// The administrator's step (D-36, D-38): runs, as <paramref name="administrator"/>, what Generate as the host left
    /// pending (the role and the actions), leaving the design's publication alone. It takes the same lease as Generate, so
    /// the two never run at once; refuses when the change list holds anything Generate itself applies (that is Generate's
    /// to do first); and reports its result on the connection row like a job. <paramref name="r"/>'s
    /// <see cref="GenerationRequest.ConfirmedFingerprint"/> is the change list the administrator saw; a different list is refused.
    /// </summary>
    /// <exception cref="GenerationBusyException">A Generate for the same source is running.</exception>
    public async Task<GenerationResult> RunAsAdministratorAsync(DataverseClient administrator, GenerationRequest r, CancellationToken ct)
    {
        var key = (r.EnvironmentId.ToLowerInvariant(), r.Design.Instance.ToLowerInvariant());
        lock (_active)
            if (_active.ContainsKey(key))
                throw new GenerationBusyException($"A Generate of {r.Design.Instance} is running; try again when it finishes.");
        var token = await _lease.TryAcquireAsync(r.EnvironmentId, r.Design.Instance, _limit + LeaseMargin, CancellationToken.None)
            ?? throw new GenerationBusyException($"A Generate of {r.Design.Instance} is running; try again when it finishes.");
        var job = new GenerationJob(Guid.NewGuid(), r.EnvironmentId, r.Design.Instance, JobState.Running, Array.Empty<string>(), null, null, DateTimeOffset.UtcNow, null,
            r.CorrelationId, ConnectionId: r.ConnectionId);
        try
        {
            var (schema, options) = await OptionsAsync(administrator, r with { ApplyRole = true }, ct);
            options = options with { Design = null };
            var hostChanges = AdministratorsOnly((await Generator.Generator.PreviewAsync(administrator, schema, options, ct)).Changes);
            if (hostChanges.Count > 0)
                throw new GenerationRefusedException(hostChanges.Select(c => $"{c} is Generate's to apply; generate the connection first").ToList());
            var result = await Generator.Generator.RunAsync(administrator, schema, options, ct);
            job = job with { State = JobState.Succeeded, Writes = result.Writes, Pending = result.Pending, DesignVersion = r.Design.Version, Finished = DateTimeOffset.UtcNow };
            return result;
        }
        catch (Exception ex) when (ex is GenerationFailedException)
        {
            job = Failed(job, ((GenerationFailedException)ex).WritesDone, ex.Message);
            throw;
        }
        finally
        {
            if (job.State != JobState.Running)
                await Report(job);
            await _lease.ReleaseAsync(r.EnvironmentId, r.Design.Instance, token, CancellationToken.None);
        }
    }

    /// <summary>The changes in <paramref name="changes"/> that are not the administrator's (actions): what Generate as the host applies.</summary>
    public static IReadOnlyList<string> AdministratorsOnly(ChangeList changes) =>
        changes.Changes.Where(c => c.Kind is not (ChangeKind.AddApi or ChangeKind.RecreateApi or ChangeKind.RemoveApi)).Select(c => c.Text).ToList();

    private async Task RunAsync(GenerationJob job, GenerationRequest r, string org, (string, string) key, string leaseToken)
    {
        DataverseClient? client = null;
        using var timeout = new CancellationTokenSource(_limit);
        try
        {
            await Report(job = job with { State = JobState.Running });
            client = clients.Create(org);
            var (schema, options) = await OptionsAsync(client, r, timeout.Token);
            var result = await Generator.Generator.RunAsync(client, schema, options, timeout.Token);
            job = job with { State = JobState.Succeeded, Writes = result.Writes, DesignVersion = result.DesignVersion, Pending = result.Pending, Finished = DateTimeOffset.UtcNow };
            configs.Invalidate(r.EnvironmentId);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            var writes = client?.Writes.ToList() ?? new List<string>();
            job = Failed(job, writes, $"Generation timed out after {Describe(_limit)}; writes made: {(writes.Count == 0 ? "none" : string.Join(", ", writes))}");
        }
        catch (GenerationFailedException ex)
        {
            job = Failed(job, ex.WritesDone, ex.Message);
        }
        catch (GenerationConfirmationException ex)
        {
            job = Failed(job, client?.Writes.ToList() ?? new List<string>(), ex.Message) with { NeedsConfirmation = true };
        }
        catch (Exception ex) when (ex is GenerationRefusedException or DataverseException or DesignFormatException
                                       or SchemaException or DescriptorException or ConfigurationException)
        {
            job = Failed(job, client?.Writes.ToList() ?? new List<string>(), ex.Message);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Generation {Instance} in {EnvironmentId} stopped unexpectedly", r.Design.Instance, r.EnvironmentId);
            job = Failed(job, client?.Writes.ToList() ?? new List<string>(), $"Generation stopped unexpectedly ({ex.GetType().Name}); the host log has the details.");
        }
        // The final state is stored before the key is freed, so a job that a new Start can follow never reads as running.
        lock (_active)
        {
            _jobs[job.Id] = job;
            _active.Remove(key);
        }
        await Report(job);
        try
        {
            await _lease.ReleaseAsync(r.EnvironmentId, r.Design.Instance, leaseToken, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // An unreleased lease only delays the next Generate until it expires.
            log.LogWarning(ex, "Generation lease for {Instance} in {EnvironmentId} could not be released; it expires by itself", r.Design.Instance, r.EnvironmentId);
        }
        _done[job.Id].TrySetResult(job);
    }

    private static GenerationJob Failed(GenerationJob job, IReadOnlyList<string> writes, string failure) =>
        job with { State = JobState.Failed, Writes = writes, Failure = failure, Finished = DateTimeOffset.UtcNow };

    private async Task Report(GenerationJob job)
    {
        _jobs[job.Id] = job;
        try
        {
            await progress.ReportAsync(job, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Progress is a report, not the job: a failed report must not change the job's result.
            log.LogWarning(ex, "Generation progress for {Instance} could not be reported", job.Instance);
        }
    }

    /// <summary>The design's schema and the options a run needs: messages from its descriptors, host URL and scope from the core setup's variables.</summary>
    private static async Task<(SourceSchema, GenerateOptions)> OptionsAsync(DataverseClient client, GenerationRequest r, CancellationToken ct)
    {
        var schema = SchemaParser.Parse(r.Design.Schema, $"{r.Design.Instance} design schema");
        var descriptors = r.Design.Descriptors.Select(d => DescriptorParser.Parse(d.Value, $"{r.Design.Instance} design descriptor {d.Key}")).ToList();
        // The host refuses at load a design whose descriptors disagree with its schema (HostConfigStore), so never generate one.
        var problems = descriptors.SelectMany(d => DescriptorValidator.Validate(d, ReservedNames.For(Array.Empty<string>()), schema).Select(e => $"{d.Operation}: {e}"))
            .Concat(schema.CustomApis.Keys.Where(a => descriptors.All(d => d.CustomApi != a)).Select(a => $"Custom API {a} is in the schema but no descriptor binds it"))
            .ToList();
        if (problems.Count > 0)
            throw new ConfigurationException($"The {r.Design.Instance} design's descriptors do not match its schema; nothing was generated: " + string.Join("; ", problems));
        var messages = descriptors.Where(d => d.CustomApi == null).Select(d => d.Binds).ToHashSet(StringComparer.Ordinal);
        var hostUrl = await VariableAsync(client, "bf_HostUrl", ct);
        var scope = await VariableAsync(client, "bf_HostScope", ct);
        return (schema, new GenerateOptions(r.Design.Instance, r.Solution, r.Publisher, hostUrl, scope, DryRun: false, messages, r.ConfirmedFingerprint,
            new DesignPublish(r.Design, r.BaseUrl, r.AuthJson), r.ApplyRole));
    }

    /// <summary>An environment variable's current value, else its default. The core setup sets bf_HostUrl and bf_HostScope.</summary>
    private static async Task<string> VariableAsync(DataverseClient client, string schema, CancellationToken ct)
    {
        var definition = (await client.ListAsync($"environmentvariabledefinitions?$select=environmentvariabledefinitionid,defaultvalue&$filter=schemaname eq {DataverseClient.Literal(schema)}", ct)).FirstOrDefault();
        if (definition != null)
        {
            var value = (await client.ListAsync($"environmentvariablevalues?$select=value&$filter=_environmentvariabledefinitionid_value eq {(string)definition["environmentvariabledefinitionid"]!}", ct)).FirstOrDefault();
            if (((string?)value?["value"] ?? (string?)definition["defaultvalue"]) is { Length: > 0 } text)
                return text;
        }
        throw new ConfigurationException($"Environment variable {schema} has no value in this environment; run the core setup (tools/dev/setup-dataverse.sh) first.");
    }

    /// <summary>The environment's Dataverse URL from Trust:AllowedCallers.</summary>
    /// <exception cref="ConfigurationException">The environment is not one the host serves, or has no OrgUrl.</exception>
    public string OrgUrlOf(string environmentId)
    {
        var caller = trust.Value.AllowedCallers.FirstOrDefault(c => string.Equals(c.EnvironmentId, environmentId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ConfigurationException($"Environment {environmentId} is not one this host serves (Trust:AllowedCallers).");
        return caller.OrgUrl is { Length: > 0 } org ? org
            : throw new ConfigurationException($"Environment {environmentId} has no OrgUrl in Trust:AllowedCallers, so the host can't reach its Dataverse.");
    }

    private void Forget()
    {
        foreach (var (id, job) in _jobs)
            if (job.Finished is { } at && DateTimeOffset.UtcNow - at > Keep)
            {
                _jobs.TryRemove(id, out _);
                _done.TryRemove(id, out _);
            }
    }

    private static string Describe(TimeSpan t) => t.TotalMinutes >= 1 ? $"{t.TotalMinutes:0} minutes" : $"{t.TotalMilliseconds:0} ms";
}
