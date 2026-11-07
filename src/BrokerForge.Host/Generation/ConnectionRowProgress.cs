using System.Text.Json.Nodes;
using BrokerForge.Host.Admin;
using BrokerForge.Host.Ingress;
using Microsoft.Extensions.Options;

namespace BrokerForge.Host.Generation;

/// <summary>
/// Reports a job on its <c>bf_connection</c> row, as the host, so the review page can show it (spec 6 step 3): state, job
/// ID, start and finish, and a result of writes, failure, pending role change, design version and whether the changes need
/// confirming. Never the design text or a secret. Jobs without a row (the admin command) only go to <paramref name="inner"/>.
/// </summary>
public sealed class ConnectionRowProgress(IGenerationProgress inner, IOptions<TrustOptions> trust, IGenerationClientFactory clients, ILogger<ConnectionRowProgress> log)
    : IGenerationProgress
{
    public async Task ReportAsync(GenerationJob job, CancellationToken ct)
    {
        await inner.ReportAsync(job, ct);
        if (job.ConnectionId is not { } id)
            return;
        var org = trust.Value.AllowedCallers.FirstOrDefault(c => string.Equals(c.EnvironmentId, job.EnvironmentId, StringComparison.OrdinalIgnoreCase))?.OrgUrl;
        if (org is not { Length: > 0 })
        {
            log.LogWarning("Generation {Instance} in {EnvironmentId} has no OrgUrl to report on its connection row", job.Instance, job.EnvironmentId);
            return;
        }
        var client = clients.Create(org);
        // A job that reports after its lease expired must not replace the status of a job started after it.
        var current = await client.GetAsync($"{ConnectionRows.EntitySet}({id:D})?$select=bf_generationjobid,bf_generationstarted", ct);
        if ((string?)current?["bf_generationjobid"] is { } other && Guid.TryParse(other, out var otherId) && otherId != job.Id
            && DateTimeOffset.TryParse((string?)current["bf_generationstarted"], out var otherStarted) && otherStarted > job.Started)
        {
            log.LogWarning("Generation {Instance} job {JobId} not reported on its row: job {OtherJob} started after it", job.Instance, job.Id, otherId);
            return;
        }
        var result = new JsonObject
        {
            ["writes"] = new JsonArray(job.Writes.Select(w => (JsonNode)JsonValue.Create(w)!).ToArray()),
            ["failure"] = job.Failure,
            ["pending"] = new JsonArray(job.Pending.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()),
            ["designVersion"] = job.DesignVersion,
            ["needsConfirmation"] = job.NeedsConfirmation,
        };
        await client.WriteAsync(HttpMethod.Patch, $"{ConnectionRows.EntitySet}({id:D})", new JsonObject
        {
            ["bf_generationstate"] = job.State.ToString(),
            ["bf_generationjobid"] = job.Id.ToString("D"),
            ["bf_generationstarted"] = job.Started,
            ["bf_generationfinished"] = job.Finished,
            ["bf_generationresult"] = result.ToJsonString(),
        }, null, ct);
    }
}
