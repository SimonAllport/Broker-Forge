namespace BrokerForge.Host.Execution;

/// <summary>
/// The end of one canonical request's budget (HLD 11: 90 seconds, inside the 2-minute sandbox limit).
/// Created once per request; retries and paging calls all draw on it. <paramref name="Clock"/> is the
/// system clock unless a test supplies one.
/// </summary>
public sealed record Deadline(DateTimeOffset At, TimeProvider? Clock = null)
{
    public static Deadline After(TimeSpan budget) => new(DateTimeOffset.UtcNow + budget);

    public TimeSpan Remaining => At - (Clock ?? TimeProvider.System).GetUtcNow();

    public bool Passed => Remaining <= TimeSpan.Zero;
}
