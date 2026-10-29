using DoiQ.Client.Models;

namespace DoiQ.Client.Services;

/// <summary>
/// The browser's in-memory copy of one board: tasks in lane order (higher SortOrder first), plus the id of the last
/// server event applied. Pure data, no I/O. A second board would get its own instance.
/// </summary>
public sealed class BoardState
{
    private readonly List<TaskVm> _tasks = [];

    public IReadOnlyList<TaskVm> Tasks => _tasks;

    /// <summary>
    /// The cursor: every event id up to and including this one has been taken in, with no hole below it. Event ids are
    /// global (all boards), so ids of other boards' events are taken in too, without being applied.
    /// </summary>
    public long LastEventId { get; private set; }

    /// <summary>Ids above the cursor that arrived early (out of order). They are applied already but cannot move the cursor past a hole.</summary>
    private readonly SortedSet<long> _early = [];

    /// <summary>True while an event above the cursor arrived before one below it: a catch-up is needed unless the missing one shows up.</summary>
    public bool HasGap => _early.Count > 0;

    public bool IsLoaded { get; private set; }

    public void Reset(IEnumerable<TaskVm> tasks, long baselineEventId)
    {
        _tasks.Clear();
        _tasks.AddRange(tasks);
        Sort();
        LastEventId = baselineEventId;
        _early.Clear();
        IsLoaded = true;
    }

    public void Clear()
    {
        _tasks.Clear();
        LastEventId = 0;
        _early.Clear();
        IsLoaded = false;
    }

    public TaskVm? Find(int id) => _tasks.Find(t => t.Id == id);

    /// <summary>True when this event was already taken in: at or below the cursor, or received early.</summary>
    public bool IsApplied(long eventId) => eventId <= LastEventId || _early.Contains(eventId);

    /// <summary>
    /// Records an event as taken in. The cursor moves only while ids are consecutive: an id beyond cursor + 1 is kept
    /// aside (<see cref="HasGap"/>) until the ids below it arrive or <see cref="CatchUpTo"/> covers them. False when the event was already taken in.
    /// </summary>
    public bool TryAdvance(long eventId)
    {
        if (IsApplied(eventId)) return false;
        _early.Add(eventId);
        Drain();
        return true;
    }

    /// <summary>A catch-up read the feed through <paramref name="cursor"/>: everything up to there is covered, holes included.</summary>
    public void CatchUpTo(long cursor)
    {
        if (cursor > LastEventId) LastEventId = cursor;
        _early.RemoveWhere(id => id <= LastEventId);
        Drain();
    }

    private void Drain()
    {
        while (_early.Remove(LastEventId + 1)) LastEventId++;
    }

    /// <summary>Adds or replaces a task. A copy older than the one held (by Version) is ignored, so a late echo cannot undo a newer change.</summary>
    public bool Upsert(TaskVm incoming)
    {
        var i = _tasks.FindIndex(t => t.Id == incoming.Id);
        if (i >= 0)
        {
            if (incoming.Version < _tasks[i].Version || incoming == _tasks[i]) return false;
            _tasks[i] = incoming;
        }
        else
        {
            _tasks.Add(incoming);
        }
        Sort();
        return true;
    }

    public bool Remove(int id) => _tasks.RemoveAll(t => t.Id == id) > 0;

    public bool AddComment(int taskId, CommentVm comment, bool unread)
    {
        var t = Find(taskId);
        if (t is null || t.Comments.Any(c => c.Id == comment.Id)) return false;
        Replace(t with { Comments = t.Comments.Append(comment).ToList(), Unread = t.Unread || unread, UpdatedAt = comment.At, UpdatedBy = comment.Author });
        return true;
    }

    /// <summary>Adds, updates or (field null) removes one info field.</summary>
    public bool ApplyField(int taskId, int fieldId, InfoFieldVm? field, bool unread)
    {
        var t = Find(taskId);
        if (t is null) return false;
        var fields = t.Fields.Where(f => f.Id != fieldId).ToList();
        if (field is not null)
        {
            var at = t.Fields.ToList().FindIndex(f => f.Id == fieldId);
            fields.Insert(at < 0 ? fields.Count : Math.Min(at, fields.Count), field);
        }
        Replace(field is null ? t with { Fields = fields, Unread = t.Unread || unread }
            : t with { Fields = fields, Unread = t.Unread || unread, UpdatedAt = field.UpdatedAt, UpdatedBy = field.Author });
        return true;
    }

    /// <summary>Moves one actor's seen position on a task forward (never back). False when nothing changed or the task is unknown.</summary>
    public bool ApplySeen(int taskId, string actor, long throughEventId)
    {
        var t = Find(taskId);
        if (t is null) return false;
        var seen = (t.SeenBy ?? []).ToList();
        var i = seen.FindIndex(s => s.Name == actor);
        if (i >= 0 && seen[i].SeenThroughEventId >= throughEventId) return false;
        if (i >= 0) seen[i] = seen[i] with { SeenThroughEventId = throughEventId };
        else seen.Add(new SeenByVm(actor, throughEventId));
        Replace(t with { SeenBy = seen });
        return true;
    }

    public bool Replace(TaskVm task)
    {
        var i = _tasks.FindIndex(t => t.Id == task.Id);
        if (i < 0) return false;
        _tasks[i] = task;
        return true;
    }

    /// <summary>Lane order, then highest SortOrder first within a lane (the server's convention); ties by id.</summary>
    private void Sort()
    {
        var sorted = _tasks.OrderBy(t => t.Lane).ThenByDescending(t => t.SortOrder).ThenBy(t => t.Id).ToList();
        _tasks.Clear();
        _tasks.AddRange(sorted);
    }
}
