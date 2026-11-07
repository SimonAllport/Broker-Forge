using BrokerForge.Connections;

namespace BrokerForge.Host.Generation;

/// <summary>
/// One Generate for one connection in one environment. <see cref="BaseUrl"/> and <see cref="AuthJson"/> are this
/// environment's own values for the design's variables; null leaves a value as it is. <see cref="ApplyRole"/> is false
/// for the host's own identity, which can't grant a generated table's privileges (HLD D-36): the role change is then
/// the job's <see cref="GenerationJob.Pending"/>. True only when an administrator runs the job as themselves.
/// <see cref="ConnectionId"/> is the <c>bf_connection</c> row the job reports on, when it was started from one (bf_Generate).
/// </summary>
public sealed record GenerationRequest(string EnvironmentId, ConnectionDesign Design, string Solution, string Publisher, string? BaseUrl, string? AuthJson,
    string? ConfirmedFingerprint, string CorrelationId, bool ApplyRole = false, Guid? ConnectionId = null);

public enum JobState { Queued, Running, Succeeded, Failed }

/// <summary>
/// A generation job as reported. <see cref="Writes"/> are the writes made; <see cref="Failure"/> says why a failed job
/// stopped. <see cref="NeedsConfirmation"/> marks a job that stopped before writing because its changes need confirming
/// (none confirmed, or not the ones confirmed): start it again with the fingerprint the failure names. <see cref="Pending"/>
/// is what a succeeded job left to an administrator (the role).
/// </summary>
public sealed record GenerationJob(Guid Id, string EnvironmentId, string Instance, JobState State, IReadOnlyList<string> Writes, int? DesignVersion,
    string? Failure, DateTimeOffset Started, DateTimeOffset? Finished, string CorrelationId, bool NeedsConfirmation = false, IReadOnlyList<string>? Pending = null,
    Guid? ConnectionId = null)
{
    public IReadOnlyList<string> Pending { get; init; } = Pending ?? Array.Empty<string>();
}

/// <summary>A job for the same environment and instance is still queued or running.</summary>
public sealed class GenerationBusyException(string message) : Exception(message);
