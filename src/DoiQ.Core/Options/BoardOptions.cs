namespace DoiQ.Core.Options;

/// <summary>Board behaviour shared by every board. Bound from the "Board" configuration section (e.g. Board__ArchiveAfterDays).</summary>
public sealed class BoardOptions
{
    public const string Section = "Board";

    /// <summary>Days a task stays visible in Done before it counts as archived.</summary>
    public int ArchiveAfterDays { get; set; } = 14;

    /// <summary>The most tasks one board load returns (a safety bound; archived tasks never count). Done tasks are left out first.</summary>
    public int MaxBoardTasks { get; set; } = 2000;

    /// <summary>Length of an agent's claim on a pool task; the agent renews it while working.</summary>
    public int ClaimLeaseMinutes { get; set; } = 15;

    /// <summary>Changes to one task within this window are sent to an agent as a single webhook.</summary>
    public int DebounceSeconds { get; set; } = 30;

    /// <summary>
    /// How many agent comments on one task may @mention another agent before the user comments again. Past it an agent's
    /// mention of an agent is refused, so two agents cannot keep waking each other. A comment by the user resets it.
    /// </summary>
    public int MaxAgentMentionHops { get; set; } = 5;
}
