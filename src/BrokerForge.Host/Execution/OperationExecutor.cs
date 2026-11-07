using BrokerForge.Abstractions;
using BrokerForge.Contracts;
using BrokerForge.Descriptors.Model;

namespace BrokerForge.Host.Execution;

/// <summary>
/// Runs one rendered request through the adapter inside the request deadline. Retries only when the
/// descriptor says the operation is idempotent, only for the statuses (or network failures) it lists,
/// and never past the deadline (descriptor spec 11, HLD 11).
/// </summary>
public sealed class OperationExecutor(IBroker broker)
{
    private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(200);

    public async Task<BrokerResult> ExecuteAsync(OperationDescriptor d, RenderedRequest req, IAuthenticatedChannel ch, Deadline deadline, string correlationId, CancellationToken ct)
    {
        var retry = d.Retry is { Idempotent: true } r ? r : null;
        var attempts = retry?.Attempts ?? 1;

        for (var attempt = 1; ; attempt++)
        {
            // One read: checking Passed and then reading Remaining again could hand CancelAfter a negative span.
            var remaining = deadline.Remaining;
            if (remaining <= TimeSpan.Zero)
                throw Timeout(d, correlationId, null);

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(remaining);
            var last = attempt >= attempts;
            try
            {
                var result = await broker.ExecuteAsync(req, ch, correlationId, budget.Token);
                if (last || retry == null || !retry.On.Contains(result.Status.ToString(System.Globalization.CultureInfo.InvariantCulture)) || !await Backoff(attempt, deadline, ct))
                    return result;
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw Timeout(d, correlationId, ex);
            }
            catch (SourceException ex) when (ex.Status == null && !last && retry != null && retry.On.Contains("network"))
            {
                if (!await Backoff(attempt, deadline, ct))
                    throw;
            }
        }
    }

    /// <summary>Waits a jittered exponential delay. Returns false when the wait would end past the deadline.</summary>
    private static async Task<bool> Backoff(int attempt, Deadline deadline, CancellationToken ct)
    {
        var delay = BaseDelay * Math.Pow(2, attempt - 1) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 100));
        if (delay >= deadline.Remaining)
            return false;
        await Task.Delay(delay, ct);
        return true;
    }

    private static SourceTimeoutException Timeout(OperationDescriptor d, string correlationId, Exception? inner) =>
        new($"The source did not answer operation {d.Operation} within the host time limit.", correlationId, inner);
}
