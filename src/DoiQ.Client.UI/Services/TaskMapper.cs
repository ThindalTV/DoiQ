using DoiQ.Client.Models;
using DoiQ.Shared.Dtos;
using DoiQ.Shared.Enums;

namespace DoiQ.Client.Services;

/// <summary>Server DTOs to the view models the board renders.</summary>
public static class TaskMapper
{
    public static BoardLane Lane(BoardStatus s) => (BoardLane)(int)s;
    public static BoardStatus Status(BoardLane l) => (BoardStatus)(int)l;

    public static DateTimeOffset At(DateTime utc) => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));

    public static AssigneeVm Assignee(TaskSummaryDto s, int meId) =>
        s.AssignedToPool ? new AssigneeVm(AssigneeKind.Pool, ClaimedBy: s.ClaimedBy)
        : s.AssigneeId is not { } id ? AssigneeVm.Unassigned
        : id == meId ? AssigneeVm.UserVm
        : AssigneeVm.ForAgent(s.Assignee ?? "Agent");

    private static AssigneeVm SubtaskAssignee(SubtaskDto s, int meId) =>
        s.AssigneeId is not { } id ? AssigneeVm.Unassigned
        : id == meId ? AssigneeVm.UserVm
        : AssigneeVm.ForAgent(s.Assignee ?? "Agent");

    /// <summary>A full task from its detail. Unread is the caller's own; "new" comments and fields are those from others since the caller last wrote.</summary>
    public static TaskVm FromDetail(TaskDetailDto d, ActorDto me)
    {
        var unread = d.Task.Unread;
        var mine = d.Comments.Where(c => c.AuthorId == me.Id).Select(c => c.CreatedAt)
            .Concat(d.Fields.Where(f => f.AuthorId == me.Id).Select(f => f.UpdatedAt))
            .DefaultIfEmpty(DateTime.MinValue).Max();

        return FromSummary(d.Task, me) with
        {
            Description = d.Description,
            CreatedBy = d.CreatedBy,
            Subtasks = d.Subtasks.Select(s => new SubtaskVm(s.Id, s.Title, s.Done, SubtaskAssignee(s, me.Id))).ToList(),
            Fields = d.Fields.Select(f => new InfoFieldVm(f.Id, f.Label, f.Value, f.Author, At(f.UpdatedAt),
                IsNew: unread && f.AuthorId != me.Id && f.UpdatedAt > mine)).ToList(),
            // Exact: the server flags a comment when it is someone else's and newer than the caller's seen position.
            Comments = d.Comments.Select(c => new CommentVm(c.Id, c.Author, c.Body, At(c.CreatedAt), IsNew: c.Unread, EventId: c.EventId, Mentions: c.Mentions)).ToList(),
            SeenBy = (d.SeenBy ?? []).Where(s => s.ActorId != me.Id).Select(s => new SeenByVm(s.Actor, s.SeenThroughEventId)).ToList(),
            Counts = null,   // the lists are here: they are the counts
        };
    }

    /// <summary>The summary-level part of a task; description, fields, comments and subtasks are left empty and the card shows the server's counts.</summary>
    public static TaskVm FromSummary(TaskSummaryDto s, ActorDto me) =>
        new(s.Id, s.Title, "", Lane(s.Status), Assignee(s, me.Id), s.DueDate, s.Source == TaskSource.Email, s.Unread,
            s.Archived, "", At(s.CreatedAt), At(s.LastActivityAt), s.LastActivityBy, [], [], [],
            s.Version, s.SortOrder, DoneAt: s.DoneAt is { } done ? At(done) : null,
            Counts: new TaskCounts(s.CommentCount, s.FieldCount, s.SubtaskTotal, s.SubtaskDone));

    /// <summary>A line of the archive view.</summary>
    public static ArchivedTaskVm ToArchived(TaskSummaryDto s, int meId) =>
        new(s.Id, s.Title, s.DoneAt is { } done ? At(done) : null, Assignee(s, meId), s.CommentCount, s.FieldCount);

    /// <summary>
    /// A hub summary laid over what the board already knows: the lists and description stay (the message carries none of them).
    /// </summary>
    public static TaskVm Merge(TaskVm existing, TaskSummaryDto s, ActorDto me, bool byOther)
    {
        var fresh = FromSummary(s, me);
        return fresh with
        {
            Description = existing.Description,
            CreatedBy = existing.CreatedBy,
            Unread = existing.Unread || byOther,
            UpdatedBy = s.LastActivityBy,
            // The caller refetches the detail when the event changed one of these lists.
            Subtasks = existing.Subtasks,
            Fields = existing.Fields,
            Comments = existing.Comments,
            SeenBy = existing.SeenBy,
            // Loaded lists stay authoritative; a task known only by its summary takes the fresh counts.
            Counts = existing.Counts is null ? null : fresh.Counts,
        };
    }
}
