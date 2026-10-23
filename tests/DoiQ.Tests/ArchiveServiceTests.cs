using DoiQ.Core.Services;
using DoiQ.Shared.Dtos;
using DoiQ.Shared.Enums;

namespace DoiQ.Tests;

/// <summary>Archive at scale: the board load, the paged archive, the board search and the paged agent search.</summary>
public class ArchiveServiceTests
{
    /// <summary>Creates a task in Done and lets the clock run past the archive window, so it is archived.</summary>
    private static async Task<int> ArchivedAsync(Harness h, int boardId, string title)
    {
        var id = await h.CreateOnAsync(boardId, title, BoardStatus.Done);
        h.Clock.Advance(TimeSpan.FromDays(h.Board.ArchiveAfterDays + 1));
        return id;
    }

    /// <summary>Archived tasks done one minute apart (the first oldest), on Main.</summary>
    private static async Task<List<int>> ArchivedManyAsync(Harness h, int count, string prefix = "old")
    {
        var ids = new List<int>();
        for (var i = 0; i < count; i++)
        {
            ids.Add(await h.CreateOnAsync(h.MainBoardId, $"{prefix} {i}", BoardStatus.Done));
            h.Clock.Advance(TimeSpan.FromMinutes(1));
        }
        h.Clock.Advance(TimeSpan.FromDays(h.Board.ArchiveAfterDays + 1));
        return ids;
    }

    [Fact]
    public async Task LoadBoard_ActiveAndArchivedTasks_ReturnsOnlyTheActiveOnesAndTheArchivedCount()
    {
        // Arrange
        using var h = new Harness();
        await ArchivedAsync(h, h.MainBoardId, "old");
        await h.CreateOnAsync(h.MainBoardId, "open");
        await h.CreateOnAsync(h.MainBoardId, "just done", BoardStatus.Done);

        // Act
        var load = await h.AsUser().LoadBoard(h.MainBoardId, h.Ct);

        // Assert
        Assert.Equal(["just done", "open"], load.Tasks.Select(t => t.Title).Order());
        Assert.Equal(1, load.ArchivedCount);
        Assert.Equal(h.Board.ArchiveAfterDays, load.ArchiveAfterDays);
        Assert.False(load.Truncated);
        Assert.All(load.Tasks, t => Assert.False(t.Archived));
    }

    [Fact]
    public async Task LoadBoard_TaskWithCommentsFieldsAndSubtasks_CarriesTheCountsTheCardShows()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateOnAsync(h.MainBoardId, "busy");
        await h.AsUser().AddComment(id, new AddCommentRequest("one"), h.Ct);
        await h.AsUser().AddComment(id, new AddCommentRequest("two"), h.Ct);
        await h.AsUser().UpsertFieldAsync(id, new FieldRequest("Notes", "x"), h.Ct);
        var sub = await h.AsUser().AddSubtask(id, new AddSubtaskRequest("step"), h.Ct);
        await h.AsUser().UpdateSubtaskAsync(id, sub.Id, new UpdateSubtaskRequest(Done: true), h.Ct);

        // Act
        var task = (await h.AsUser().LoadBoard(h.MainBoardId, h.Ct)).Tasks.Single();

        // Assert
        Assert.Equal((2, 1, 1, 1), (task.CommentCount, task.FieldCount, task.SubtaskTotal, task.SubtaskDone));
    }

    [Fact]
    public async Task LoadBoard_TwoBoards_ReturnsAndCountsOnlyThatBoard()
    {
        // Arrange
        using var h = new Harness();
        var work = await h.AddBoardAsync("Work");
        await h.CreateOnAsync(work, "work open");
        await ArchivedAsync(h, work, "work old");
        await ArchivedAsync(h, h.MainBoardId, "main old");
        await h.CreateOnAsync(h.MainBoardId, "main open");

        // Act
        var load = await h.AsUser().LoadBoard(work, h.Ct);

        // Assert
        Assert.Equal(["work open"], load.Tasks.Select(t => t.Title));
        Assert.Equal(1, load.ArchivedCount);
        Assert.Equal(work, load.BoardId);
    }

    [Fact]
    public async Task LoadBoard_MoreActiveTasksThanTheLimit_LeavesDoneTasksOutFirstAndSaysSo()
    {
        // Arrange
        using var h = new Harness();
        h.Board.MaxBoardTasks = 2;
        await h.CreateOnAsync(h.MainBoardId, "done", BoardStatus.Done);
        await h.CreateOnAsync(h.MainBoardId, "new");
        await h.CreateOnAsync(h.MainBoardId, "doing", BoardStatus.Doing);

        // Act
        var load = await h.AsUser().LoadBoard(h.MainBoardId, h.Ct);

        // Assert
        Assert.True(load.Truncated);
        Assert.Equal(["doing", "new"], load.Tasks.Select(t => t.Title).Order());
    }

    [Fact]
    public async Task LoadBoard_LatestEventId_IsTheNewestEventBeforeTheLoad()
    {
        // Arrange
        using var h = new Harness();
        await h.CreateOnAsync(h.MainBoardId, "a");
        var latest = await h.AsUser().GetLatestEventIdAsync(null, h.Ct);

        // Act
        var load = await h.AsUser().LoadBoard(h.MainBoardId, h.Ct);

        // Assert
        Assert.Equal(latest, load.LatestEventId);
    }

    [Fact]
    public async Task LoadBoard_UnknownBoard_ThrowsNotFound()
    {
        // Arrange
        using var h = new Harness();

        // Act
        var ex = await Record.ExceptionAsync(() => h.AsUser().LoadBoard(999, h.Ct));

        // Assert
        Assert.IsType<NotFoundException>(ex);
    }

    [Fact]
    public async Task GetArchive_MoreThanAPage_PagesNewestDoneFirstWithoutRepeatsOrGaps()
    {
        // Arrange
        using var h = new Harness();
        var ids = await ArchivedManyAsync(h, 5);
        var svc = h.AsUser();
        var seen = new List<int>();

        // Act
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await svc.GetArchive(h.MainBoardId, null, cursor, 2, h.Ct);
            seen.AddRange(page.Tasks.Select(t => t.Id));
            cursor = page.NextCursor;
            pages++;
        } while (cursor is not null);

        // Assert
        Assert.Equal(Enumerable.Reverse(ids), seen);
        Assert.Equal(3, pages);
    }

    [Fact]
    public async Task GetArchive_TaskReopenedBetweenPages_NextPageContinuesAfterTheLastOneShown()
    {
        // Arrange
        using var h = new Harness();
        var ids = await ArchivedManyAsync(h, 5);   // newest first: ids[4], ids[3] | ids[2], ids[1] | ids[0]
        var first = await h.AsUser().GetArchive(h.MainBoardId, null, null, 2, h.Ct);
        await h.AsUser().Move(ids[4], new MoveTaskRequest(BoardStatus.New), h.Ct);   // leaves the archive (it was on page one)

        // Act
        var second = await h.AsUser().GetArchive(h.MainBoardId, null, first.NextCursor, 2, h.Ct);

        // Assert
        Assert.Equal([ids[2], ids[1]], second.Tasks.Select(t => t.Id));
    }

    [Fact]
    public async Task GetArchive_Query_SearchesTheTextOfArchivedTasksOnly()
    {
        // Arrange
        using var h = new Harness();
        var inComment = await h.CreateOnAsync(h.MainBoardId, "first", BoardStatus.Done);
        await h.AsUser().AddComment(inComment, new AddCommentRequest("mentions the plumber"), h.Ct);
        var inField = await h.CreateOnAsync(h.MainBoardId, "second", BoardStatus.Done);
        await h.AsUser().UpsertFieldAsync(inField, new FieldRequest("Who", "Plumber Ltd"), h.Ct);
        await h.CreateOnAsync(h.MainBoardId, "third", BoardStatus.Done);
        h.Clock.Advance(TimeSpan.FromDays(h.Board.ArchiveAfterDays + 1));
        await h.CreateOnAsync(h.MainBoardId, "call the plumber");   // active: not in the archive

        // Act
        var page = await h.AsUser().GetArchive(h.MainBoardId, "PLUMBER", null, 50, h.Ct);

        // Assert
        Assert.Equal([inComment, inField], page.Tasks.Select(t => t.Id).Order());
        Assert.All(page.Tasks, t => Assert.True(t.Archived));
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task GetArchive_CursorThatWasNotIssued_ThrowsValidation()
    {
        // Arrange
        using var h = new Harness();

        // Act
        var ex = await Record.ExceptionAsync(() => h.AsUser().GetArchive(h.MainBoardId, null, "not-a-cursor", 50, h.Ct));

        // Assert
        Assert.IsType<ValidationException>(ex);
    }

    [Fact]
    public async Task GetArchive_TaskInDoneWithinTheWindow_IsNotArchived()
    {
        // Arrange
        using var h = new Harness();
        await h.CreateOnAsync(h.MainBoardId, "recent", BoardStatus.Done);
        h.Clock.Advance(TimeSpan.FromDays(h.Board.ArchiveAfterDays - 1));

        // Act
        var page = await h.AsUser().GetArchive(h.MainBoardId, null, null, 50, h.Ct);

        // Assert
        Assert.Empty(page.Tasks);
    }

    [Fact]
    public async Task SearchBoard_TextInActiveAndArchivedTasks_ReturnsTheActiveIdsAndTheArchivedGroup()
    {
        // Arrange
        using var h = new Harness();
        var old = await ArchivedAsync(h, h.MainBoardId, "garden fence");
        var active = await h.CreateOnAsync(h.MainBoardId, "water the garden");
        await h.CreateOnAsync(h.MainBoardId, "unrelated");

        // Act
        var found = await h.AsUser().SearchBoard(h.MainBoardId, "garden", 10, h.Ct);

        // Assert
        Assert.Equal([active], found.ActiveTaskIds);
        Assert.Equal([old], found.Archived.Tasks.Select(t => t.Id));
        Assert.Null(found.Archived.NextCursor);
    }

    [Fact]
    public async Task SearchBoard_MoreArchivedMatchesThanTheLimit_ReturnsTheNewestAndACursor()
    {
        // Arrange
        using var h = new Harness();
        var ids = await ArchivedManyAsync(h, 4, "garden");

        // Act
        var found = await h.AsUser().SearchBoard(h.MainBoardId, "garden", 3, h.Ct);

        // Assert
        Assert.Equal([ids[3], ids[2], ids[1]], found.Archived.Tasks.Select(t => t.Id));
        Assert.NotNull(found.Archived.NextCursor);
    }

    [Fact]
    public async Task SearchBoard_MatchInAChecklistLine_FindsTheTask()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateOnAsync(h.MainBoardId, "trip");
        await h.AsUser().AddSubtask(id, new AddSubtaskRequest("pack the passport"), h.Ct);

        // Act
        var found = await h.AsUser().SearchBoard(h.MainBoardId, "passport", 10, h.Ct);

        // Assert
        Assert.Equal([id], found.ActiveTaskIds);
    }

    [Fact]
    public async Task SearchBoard_EmptyText_ThrowsValidation()
    {
        // Arrange
        using var h = new Harness();

        // Act
        var ex = await Record.ExceptionAsync(() => h.AsUser().SearchBoard(h.MainBoardId, " ", 10, h.Ct));

        // Assert
        Assert.IsType<ValidationException>(ex);
    }

    [Fact]
    public async Task Move_ArchivedTaskOutOfDone_PutsItBackOnTheBoardAndBroadcastsItAsActive()
    {
        // Arrange
        using var h = new Harness();
        var id = await ArchivedAsync(h, h.MainBoardId, "reopen me");

        // Act
        var moved = await h.AsUser().Move(id, new MoveTaskRequest(BoardStatus.Waiting), h.Ct);

        // Assert
        Assert.False(moved.Archived);
        Assert.Null(moved.DoneAt);
        Assert.False(h.Broadcaster.Changed[^1].Task.Archived);
        var load = await h.AsUser().LoadBoard(h.MainBoardId, h.Ct);
        Assert.Contains(load.Tasks, t => t.Id == id);
        Assert.Equal(0, load.ArchivedCount);
    }

    [Fact]
    public async Task UpdateAsync_ArchivedTask_BroadcastsItsSummaryAsArchived()
    {
        // Arrange
        using var h = new Harness();
        var id = await ArchivedAsync(h, h.MainBoardId, "old");

        // Act
        await h.AsUser().UpdateAsync(id, new UpdateTaskRequest(Title: "old, renamed"), h.Ct);

        // Assert
        var pushed = h.Broadcaster.Changed[^1].Task;
        Assert.Equal(id, pushed.Id);
        Assert.True(pushed.Archived);   // a browser must not put it back on the board
    }

    [Fact]
    public async Task GetAsync_ArchivedTask_SaysItIsArchived()
    {
        // Arrange
        using var h = new Harness();
        var id = await ArchivedAsync(h, h.MainBoardId, "old");

        // Act
        var detail = await h.AsUser().GetAsync(id, h.Ct);

        // Assert
        Assert.True(detail.Task.Archived);
    }

    [Fact]
    public async Task GetInbox_UnreadArchivedTask_IsLeftOut()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateOnAsync(h.MainBoardId, "old", BoardStatus.Done, assignee: "Hermes");
        await h.AsHermes().AddComment(id, new AddCommentRequest("finished"), h.Ct);   // unread for the user
        h.Clock.Advance(TimeSpan.FromDays(h.Board.ArchiveAfterDays + 1));

        // Act
        var inbox = await h.AsUser().GetInboxAsync(h.Ct);

        // Assert
        Assert.DoesNotContain(inbox, i => i.Task.Id == id);
    }

    [Fact]
    public async Task Search_MoreMatchesThanTheLimit_PagesInTheRequestedOrderWithACursor()
    {
        // Arrange
        using var h = new Harness();
        foreach (var t in new[] { "delta", "alpha", "charlie", "bravo", "echo" }) await h.CreateOnAsync(h.MainBoardId, t);
        var query = new TaskQuery { Sort = "title" };
        var titles = new List<string>();

        // Act
        string? cursor = null;
        do
        {
            var page = await h.AsUser().Search(query, cursor, 2, h.Ct);
            titles.AddRange(page.Tasks.Select(t => t.Title));
            cursor = page.NextCursor;
        } while (cursor is not null);

        // Assert
        Assert.Equal(["alpha", "bravo", "charlie", "delta", "echo"], titles);
    }

    [Fact]
    public async Task Search_TaskAddedBetweenPages_DoesNotShiftTheNextPage()
    {
        // Arrange
        using var h = new Harness();
        foreach (var t in new[] { "b", "d", "f" }) await h.CreateOnAsync(h.MainBoardId, t);
        var query = new TaskQuery { Sort = "title" };
        var first = await h.AsUser().Search(query, null, 2, h.Ct);   // b, d
        await h.CreateOnAsync(h.MainBoardId, "a");                     // sorts before the cursor

        // Act
        var second = await h.AsUser().Search(query, first.NextCursor, 2, h.Ct);

        // Assert
        Assert.Equal(["f"], second.Tasks.Select(t => t.Title));
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public async Task Search_WithoutIncludeArchived_LeavesArchivedTasksOut()
    {
        // Arrange
        using var h = new Harness();
        await ArchivedAsync(h, h.MainBoardId, "old");
        await h.CreateOnAsync(h.MainBoardId, "new");

        // Act
        var page =await h.AsUser().Search(new TaskQuery(), null, 50, h.Ct);

        // Assert
        Assert.Equal(["new"], page.Tasks.Select(t => t.Title));
    }

    [Fact]
    public async Task Search_CursorFromAnotherSort_ThrowsValidation()
    {
        // Arrange
        using var h = new Harness();
        foreach (var t in new[] { "a", "b", "c" }) await h.CreateOnAsync(h.MainBoardId, t);
        var first = await h.AsUser().Search(new TaskQuery { Sort = "title" }, null, 1, h.Ct);

        // Act
        var ex = await Record.ExceptionAsync(() => h.AsUser().Search(new TaskQuery { Sort = "created" }, first.NextCursor, 1, h.Ct));

        // Assert
        Assert.IsType<ValidationException>(ex);
    }

    [Fact]
    public async Task Search_IncludeArchived_ReturnsArchivedTasksFlagged()
    {
        // Arrange
        using var h = new Harness();
        var old = await ArchivedAsync(h, h.MainBoardId, "old");
        await h.CreateOnAsync(h.MainBoardId, "new");

        // Act
        var all = await h.AsUser().Search(new TaskQuery { IncludeArchived = true }, null, 50, h.Ct);

        // Assert
        Assert.Equal(2, all.Tasks.Count);
        Assert.True(all.Tasks.Single(t => t.Id == old).Archived);
    }
}
