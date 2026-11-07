namespace BrokerForge.Host.Generation;

/// <summary>Where a job's progress goes. Plan 4 adds the connection-row writer; the default logs it.</summary>
public interface IGenerationProgress
{
    Task ReportAsync(GenerationJob job, CancellationToken ct);
}

/// <summary>Logs each state with the correlation ID. Never the design text: only the instance, state, counts and failure.</summary>
public sealed class LoggingGenerationProgress(ILogger<LoggingGenerationProgress> log) : IGenerationProgress
{
    public Task ReportAsync(GenerationJob job, CancellationToken ct)
    {
        using (log.BeginScope(new Dictionary<string, object> { ["correlationId"] = job.CorrelationId }))
        {
            if (job.State == JobState.Failed)
                log.LogWarning("Generation {Instance} in {EnvironmentId}: {State} after {Writes} write(s): {Failure}", job.Instance, job.EnvironmentId, job.State, job.Writes.Count, job.Failure);
            else
                log.LogInformation("Generation {Instance} in {EnvironmentId}: {State}, {Writes} write(s){Version}", job.Instance, job.EnvironmentId, job.State, job.Writes.Count,
                    job.DesignVersion is { } v ? $", design v{v}" : "");
        }
        return Task.CompletedTask;
    }
}
