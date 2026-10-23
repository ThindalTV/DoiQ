namespace DoiQ.Core.Outbox;

/// <summary>
/// Bound from the "Outbox" configuration section (Outbox__RetryBaseSeconds, ...). The 30 s per-task debounce lives in
/// BoardOptions.DebounceSeconds because it is applied when a row is queued, not when it is delivered.
/// </summary>
public class OutboxOptions
{
    public const string Section = "Outbox";

    /// <summary>First retry delay after a failed delivery; doubles with every further attempt.</summary>
    public double RetryBaseSeconds { get; set; } = 10;

    /// <summary>Upper bound for the retry delay.</summary>
    public double RetryMaxSeconds { get; set; } = 3600;

    /// <summary>After this many failed attempts the row is closed and keeps its LastError.</summary>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>The worker wakes on a signal but also polls at least this often (debounce and retries are time based).</summary>
    public double PollSeconds { get; set; } = 5;
}
