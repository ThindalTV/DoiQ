using DoiQ.Client.Models;

namespace DoiQ.Client.Tests;

/// <summary>Builders for view models, so a test states only what it cares about.</summary>
public static class Tasks
{
    public static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    public static TaskVm Make(
        int id = 1,
        string title = "A task",
        string description = "",
        BoardLane lane = BoardLane.New,
        AssigneeVm? assignee = null,
        DateOnly? due = null,
        bool fromEmail = false,
        bool unread = false,
        bool archived = false,
        string updatedBy = Fmt.HumanServerName,
        IReadOnlyList<SubtaskVm>? subtasks = null,
        IReadOnlyList<InfoFieldVm>? fields = null,
        IReadOnlyList<CommentVm>? comments = null,
        IReadOnlyList<SeenByVm>? seenBy = null) =>
        new(id, title, description, lane, assignee ?? AssigneeVm.Unassigned, due, fromEmail, unread, archived,
            Fmt.HumanServerName, Now, Now, updatedBy, subtasks ?? [], fields ?? [], comments ?? [], SeenBy: seenBy);

    public static SubtaskVm Subtask(int id, bool done = false, string? title = null) =>
        new(id, title ?? $"Step {id}", done, AssigneeVm.UserVm);

    public static CommentVm Comment(int id, string author = "Hermes", string body = "A comment", bool isNew = false, long eventId = 0) =>
        new(id, author, body, Now, isNew, eventId);

    public static InfoFieldVm Field(int id, string name = "Notes", string body = "Details", bool isNew = false) =>
        new(id, name, body, "Hermes", Now, isNew);

    public static BoardDto Board(int id, string name, int open = 0, int done = 0) =>
        new(id, name, open, done, DateTime.UtcNow, 1);
}
