using System.Globalization;

namespace DoiQ.Client.Models;

public enum BoardLane { New, Waiting, Doing, Done }

public enum AssigneeKind { None, User, Agent, Pool }

/// <summary>Who a task or subtask belongs to. Pool = "any agent", optionally already claimed.</summary>
public sealed record AssigneeVm(AssigneeKind Kind, string? Agent = null, string? ClaimedBy = null)
{
    public static AssigneeVm Unassigned { get; } = new(AssigneeKind.None);
    public static AssigneeVm UserVm { get; } = new(AssigneeKind.User);
    public static AssigneeVm AnyAgent { get; } = new(AssigneeKind.Pool);
    public static AssigneeVm ForAgent(string name) => new(AssigneeKind.Agent, name);

    public string Label => Kind switch
    {
        AssigneeKind.User => Fmt.HumanDisplay,
        AssigneeKind.Agent => Agent ?? "Agent",
        AssigneeKind.Pool => ClaimedBy is null ? "Any agent" : $"Any agent, claimed by {ClaimedBy}",
        _ => "Unassigned",
    };

    /// <summary>"Hermes" becomes "H", "Hermes 2" becomes "H2".</summary>
    public static string Initials(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var digits = new string(name.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return char.ToUpperInvariant(name.Trim()[0]) + digits;
    }
}

public sealed record SubtaskVm(int Id, string Title, bool Done, AssigneeVm Assignee);

public sealed record InfoFieldVm(int Id, string Name, string Body, string Author, DateTimeOffset UpdatedAt, bool IsNew);

/// <summary>EventId = the server event that created the comment (0 if unknown); IsNew = written by someone else after the caller's seen position.</summary>
public sealed record CommentVm(int Id, string Author, string Body, DateTimeOffset At, bool IsNew, long EventId = 0)
{
    public bool IsUser => Author == Fmt.HumanServerName;
}

/// <summary>How far another actor has seen a task (everything through that event id).</summary>
public sealed record SeenByVm(string Name, long SeenThroughEventId);

public sealed record PresenceVm(int ActiveAgents, string? LastAgent, DateTimeOffset? LastSeen);

public sealed record TaskVm(
    int Id,
    string Title,
    string Description,
    BoardLane Lane,
    AssigneeVm Assignee,
    DateOnly? Due,
    bool FromEmail,
    bool Unread,
    bool Archived,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string UpdatedBy,
    IReadOnlyList<SubtaskVm> Subtasks,
    IReadOnlyList<InfoFieldVm> Fields,
    IReadOnlyList<CommentVm> Comments,
    long Version = 0,
    double SortOrder = 0,
    IReadOnlyList<SeenByVm>? SeenBy = null,
    DateTimeOffset? DoneAt = null,
    /// <summary>The server's counts for a task loaded without its lists (the board load); null once the lists are loaded.</summary>
    TaskCounts? Counts = null)
{
    public int SubtasksDone => Subtasks.Count(s => s.Done);

    // What a card shows: the server's counts until the lists are loaded (the drawer loads them on open), then the lists.
    public int CommentCount => Counts?.Comments ?? Comments.Count;
    public int FieldCount => Counts?.Fields ?? Fields.Count;
    public int SubtaskCount => Counts?.Subtasks ?? Subtasks.Count;
    public int SubtaskDoneCount => Counts?.SubtasksDone ?? SubtasksDone;

    /// <summary>Names of the other actors that have seen the comment (the "Seen by" receipt under the user's own comments).</summary>
    public IReadOnlyList<string> SeenByFor(CommentVm c) =>
        c.EventId <= 0 || SeenBy is null ? [] : SeenBy.Where(s => s.SeenThroughEventId >= c.EventId).Select(s => Fmt.Person(s.Name)).ToList();
}

public sealed record TaskCounts(int Comments, int Fields, int Subtasks, int SubtasksDone);

/// <summary>One line of the archive view or of the "Archived" search group.</summary>
public sealed record ArchivedTaskVm(int Id, string Title, DateTimeOffset? DoneAt, AssigneeVm Assignee, int CommentCount, int FieldCount);

/// <summary>A page of archived tasks; NextCursor fetches the next one (null: that was the last).</summary>
public sealed record ArchivePageVm(IReadOnlyList<ArchivedTaskVm> Tasks, string? NextCursor);

/// <summary>The board search: which active tasks match (they are on the board), and the first archived matches.</summary>
public sealed record BoardSearchVm(string Query, IReadOnlySet<int> ActiveIds, IReadOnlyList<ArchivedTaskVm> Archived, bool MoreArchived);

public enum AssigneeFilter { All, Me, Agents, Unassigned }

public enum SortMode { LaneOrder, RecentlyUpdated, Created, DueDate, Title }

/// <summary>Mutable filter state shared between the top bar and the board.</summary>
public sealed class BoardFilter
{
    public string Search { get; set; } = "";
    public AssigneeFilter Assignee { get; set; } = AssigneeFilter.All;
    /// <summary>When Assignee is Agents: a specific agent name, or null for any agent.</summary>
    public string? AgentName { get; set; }
    public bool NeedsMe { get; set; }
    public SortMode Sort { get; set; } = SortMode.LaneOrder;

    /// <summary>Only the priority order can be edited: under any other sort the visible order is not the stored one.</summary>
    public bool CanReorder => Sort == SortMode.LaneOrder;

    public static string SortName(SortMode s) => s switch
    {
        SortMode.RecentlyUpdated => "recently updated",
        SortMode.Created => "created",
        SortMode.DueDate => "due date",
        SortMode.Title => "title",
        _ => "lane order",
    };
}

public sealed record DropTarget(BoardLane Lane, int? BeforeId);

public static class Fmt
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The human's seeded name on the server (agents and webhooks see it); the UI always says "You".</summary>
    public const string HumanServerName = "User";
    public const string HumanDisplay = "You";
    public const string HumanInitial = "Y";

    /// <summary>Display name for an actor name coming from the server.</summary>
    public static string Person(string name) => name == HumanServerName ? HumanDisplay : name;

    public static string Ago(DateTimeOffset t)
    {
        var d = DateTimeOffset.Now - t;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        return t.LocalDateTime.Date == DateTime.Today
            ? t.LocalDateTime.ToString("HH:mm", Inv)
            : t.LocalDateTime.ToString("MMM d HH:mm", Inv);
    }

    public static string When(DateTimeOffset t) =>
        t.LocalDateTime.Date == DateTime.Today
            ? "today " + t.LocalDateTime.ToString("HH:mm", Inv)
            : t.LocalDateTime.ToString("MMM d HH:mm", Inv);

    public static string Due(DateOnly d) => d.ToString("MMM d", Inv);

    /// <summary>A date with the year when it is not this year ("Mar 4", "Mar 4 2025").</summary>
    public static string Day(DateTimeOffset t) =>
        t.LocalDateTime.Year == DateTime.Today.Year ? t.LocalDateTime.ToString("MMM d", Inv) : t.LocalDateTime.ToString("MMM d yyyy", Inv);
}
