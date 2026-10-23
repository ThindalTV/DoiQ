using DoiQ.Core.Services;
using DoiQ.Shared.Dtos;
using DoiQ.Shared.Enums;
using Microsoft.EntityFrameworkCore;

namespace DoiQ.Tests;

/// <summary>Boards: names, the user-only rule, the delete rules, and that every read and write stays on its own board.</summary>
public sealed class BoardServiceTests
{
    // ───────────────────────── names and rights ─────────────────────────

    [Fact]
    public async Task ListAsync_FreshDatabase_HasOneBoardNamedMain()
    {
        // Arrange
        using var h = new Harness();

        // Act
        var boards = await h.BoardsAsUser().ListAsync();

        // Assert
        var main = Assert.Single(boards);
        Assert.Equal("Main", main.Name);
    }

    [Fact]
    public async Task CreateAsync_NameWithSurroundingSpaces_StoresItTrimmed()
    {
        // Arrange
        using var h = new Harness();

        // Act
        var board = await h.BoardsAsUser().CreateAsync(new CreateBoardRequest("  Work  "));

        // Assert
        Assert.Equal("Work", board.Name);
    }

    [Fact]
    public async Task CreateAsync_BlankName_IsRejected()
    {
        // Arrange
        using var h = new Harness();

        // Act
        var ex = await Record.ExceptionAsync(() => h.BoardsAsUser().CreateAsync(new CreateBoardRequest("   ")));

        // Assert
        Assert.IsType<ValidationException>(ex);
    }

    [Fact]
    public async Task CreateAsync_NameOfSixtyCharacters_IsAccepted()
    {
        // Arrange
        using var h = new Harness();

        // Act
        var board = await h.BoardsAsUser().CreateAsync(new CreateBoardRequest(new string('a', 60)));

        // Assert
        Assert.Equal(60, board.Name.Length);
    }

    [Fact]
    public async Task CreateAsync_NameOfSixtyOneCharacters_IsRejected()
    {
        // Arrange
        using var h = new Harness();

        // Act
        var ex = await Record.ExceptionAsync(() => h.BoardsAsUser().CreateAsync(new CreateBoardRequest(new string('a', 61))));

        // Assert
        Assert.IsType<ValidationException>(ex);
    }

    [Fact]
    public async Task CreateAsync_NameThatDiffersOnlyByCase_IsRefusedAsDuplicate()
    {
        // Arrange
        using var h = new Harness();
        await h.AddBoardAsync("Work");

        // Act
        var ex = await Record.ExceptionAsync(() => h.BoardsAsUser().CreateAsync(new CreateBoardRequest("wORK")));

        // Assert
        Assert.IsType<ConflictException>(ex);
    }

    [Fact]
    public async Task CreateAsync_NonAsciiNameThatDiffersOnlyByCase_IsRefusedAsDuplicate()
    {
        // Arrange
        using var h = new Harness();
        await h.AddBoardAsync("Åre");

        // Act
        var ex = await Record.ExceptionAsync(() => h.BoardsAsUser().CreateAsync(new CreateBoardRequest("åRE")));

        // Assert
        Assert.IsType<ConflictException>(ex);
    }

    [Fact]
    public async Task CreateAsync_ByAnAgent_IsForbidden()
    {
        // Arrange
        using var h = new Harness();

        // Act
        var ex = await Record.ExceptionAsync(() => h.BoardsAs(h.Hermes).CreateAsync(new CreateBoardRequest("Sneaky")));

        // Assert
        Assert.IsType<ForbiddenException>(ex);
    }

    [Fact]
    public async Task RenameAsync_ByAnAgent_IsForbidden()
    {
        // Arrange
        using var h = new Harness();

        // Act
        var ex = await Record.ExceptionAsync(() => h.BoardsAs(h.Hermes).RenameAsync(h.MainBoardId, new RenameBoardRequest("Renamed")));

        // Assert
        Assert.IsType<ForbiddenException>(ex);
    }

    [Fact]
    public async Task DeleteAsync_ByAnAgent_IsForbidden()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.AddBoardAsync("Spare");

        // Act
        var ex = await Record.ExceptionAsync(() => h.BoardsAs(h.Hermes).DeleteAsync(id, confirm: true));

        // Assert
        Assert.IsType<ForbiddenException>(ex);
    }

    [Fact]
    public async Task ListAsync_ByAnAgent_ReturnsTheBoards()
    {
        // Arrange
        using var h = new Harness();
        await h.AddBoardAsync("Work");

        // Act
        var boards = await h.BoardsAs(h.Hermes).ListAsync();

        // Assert
        Assert.Equal(["Main", "Work"], boards.Select(b => b.Name));
    }

    [Fact]
    public async Task RenameAsync_ToAnotherBoardsName_IsRefused()
    {
        // Arrange
        using var h = new Harness();
        var work = await h.AddBoardAsync("Work");

        // Act
        var ex = await Record.ExceptionAsync(() => h.BoardsAsUser().RenameAsync(work, new RenameBoardRequest("MAIN")));

        // Assert
        Assert.IsType<ConflictException>(ex);
    }

    [Fact]
    public async Task RenameAsync_OnlyChangingTheCaseOfItsOwnName_Works()
    {
        // Arrange
        using var h = new Harness();
        var work = await h.AddBoardAsync("work");

        // Act
        var renamed = await h.BoardsAsUser().RenameAsync(work, new RenameBoardRequest("Work"));

        // Assert
        Assert.Equal("Work", renamed.Name);
    }

    [Fact]
    public async Task RenameAsync_WithAStaleVersion_IsRefusedAndKeepsTheOtherName()
    {
        // Arrange
        using var h = new Harness();
        var work = await h.AddBoardAsync("Work");
        var seen = (await h.BoardsAsUser().ListAsync()).Single(b => b.Id == work).Version;
        await h.BoardsAsUser().RenameAsync(work, new RenameBoardRequest("Job", seen));   // another tab got there first

        // Act
        var ex = await Record.ExceptionAsync(() => h.BoardsAsUser().RenameAsync(work, new RenameBoardRequest("Office", seen)));

        // Assert
        Assert.IsType<ConflictException>(ex);
        Assert.Equal("Job", (await h.BoardsAsUser().ListAsync()).Single(b => b.Id == work).Name);
    }

    [Fact]
    public async Task CreateAsync_Always_BroadcastsTheNewBoard()
    {
        // Arrange
        using var h = new Harness();

        // Act
        var id = await h.AddBoardAsync("Work");

        // Assert
        var message = Assert.Single(h.Broadcaster.BoardChanges);
        Assert.Equal((BoardChangeKind.Created, id, "Work"), (message.Kind, message.BoardId, message.Name));
    }

    // ───────────────────────── deleting ─────────────────────────

    [Fact]
    public async Task DeleteAsync_WithoutConfirmation_ChangesNothing()
    {
        // Arrange
        using var h = new Harness();
        var work = await h.AddBoardAsync("Work");

        // Act
        var ex = await Record.ExceptionAsync(() => h.BoardsAsUser().DeleteAsync(work, confirm: false));

        // Assert
        Assert.IsType<ValidationException>(ex);
        Assert.Equal(2, (await h.BoardsAsUser().ListAsync()).Count);
    }

    [Fact]
    public async Task DeleteAsync_BoardWithAnOpenTask_IsRefusedWithTheCount()
    {
        // Arrange
        using var h = new Harness();
        var work = await h.AddBoardAsync("Work");
        await h.CreateOnAsync(work, "Still to do", BoardStatus.Doing);
        await h.CreateOnAsync(work, "Not started");
        await h.CreateOnAsync(work, "Finished", BoardStatus.Done);

        // Act
        var ex = await Record.ExceptionAsync(() => h.BoardsAsUser().DeleteAsync(work, confirm: true));

        // Assert
        var refused = Assert.IsType<BoardNotEmptyException>(ex);
        Assert.Equal(2, refused.OpenTasks);
        Assert.Contains("2 tasks", refused.Message);
        Assert.Equal(3, (await h.AsUser().List(new TaskQuery { BoardId = work }, h.Ct)).Count);
    }

    [Fact]
    public async Task DeleteAsync_TheLastBoard_IsRefused()
    {
        // Arrange
        using var h = new Harness();

        // Act
        var ex = await Record.ExceptionAsync(() => h.BoardsAsUser().DeleteAsync(h.MainBoardId, confirm: true));

        // Assert
        var refused = Assert.IsType<ConflictException>(ex);
        Assert.Contains("last", refused.Message);
    }

    [Fact]
    public async Task DeleteAsync_UnknownBoard_IsNotFound()
    {
        // Arrange
        using var h = new Harness();

        // Act
        var ex = await Record.ExceptionAsync(() => h.BoardsAsUser().DeleteAsync(999, confirm: true));

        // Assert
        Assert.IsType<NotFoundException>(ex);
    }

    [Fact]
    public async Task DeleteAsync_BoardWhereEverythingIsDone_RemovesItWithAllItsData()
    {
        // Arrange
        using var h = new Harness();
        var work = await h.AddBoardAsync("Work");
        var done = await h.CreateOnAsync(work, "Wrapped up", BoardStatus.Done, assignee: "Hermes");   // queues an outbox row for Hermes
        await h.AsUser().AddComment(done, new AddCommentRequest("A note"));
        await h.AsUser().UpsertFieldAsync(done, new FieldRequest("Result", "ok"));
        await h.AsUser().AddSubtask(done, new AddSubtaskRequest("Step"));
        await h.AsHermes().MarkSeen(done, null);
        var keep = await h.CreateOnAsync(h.MainBoardId, "Stays", assignee: "Hermes");
        await h.AsUser().AddComment(keep, new AddCommentRequest("Also stays"));

        // Act
        await h.BoardsAsUser().DeleteAsync(work, confirm: true);

        // Assert
        await using var db = h.NewDb();
        Assert.Equal(["Main"], await db.Boards.Select(b => b.Name).ToListAsync());
        Assert.Equal([keep], await db.Tasks.IgnoreQueryFilters().Select(t => t.Id).ToListAsync());
        Assert.Equal(1, await db.Comments.CountAsync());
        Assert.Equal(0, await db.Fields.CountAsync());
        Assert.Equal(0, await db.Subtasks.CountAsync());
        Assert.All(await db.Events.ToListAsync(), e => Assert.Equal(h.MainBoardId, e.BoardId));
        Assert.All(await db.Outbox.ToListAsync(), o => Assert.Equal(h.MainBoardId, o.BoardId));
        Assert.DoesNotContain(await db.ReadMarkers.ToListAsync(), m => m.TaskId == done);
    }

    [Fact]
    public async Task DeleteAsync_Success_BroadcastsTheDeletion()
    {
        // Arrange
        using var h = new Harness();
        var work = await h.AddBoardAsync("Work");

        // Act
        await h.BoardsAsUser().DeleteAsync(work, confirm: true);

        // Assert
        Assert.Contains(h.Broadcaster.BoardChanges, m => m.Kind == BoardChangeKind.Deleted && m.BoardId == work && m.Name == "Work");
    }

    [Fact]
    public async Task ListAsync_BoardsWithTasks_CountsOpenAndDonePerBoard()
    {
        // Arrange
        using var h = new Harness();
        var work = await h.AddBoardAsync("Work");
        await h.CreateOnAsync(work, "a");
        await h.CreateOnAsync(work, "b", BoardStatus.Waiting);
        await h.CreateOnAsync(work, "c", BoardStatus.Done);
        await h.CreateOnAsync(h.MainBoardId, "d");

        // Act
        var boards = await h.BoardsAsUser().ListAsync();

        // Assert
        Assert.Equal([(1, 0), (2, 1)], boards.Select(b => (b.OpenTasks, b.DoneTasks)));
    }

    // ───────────────────────── separation ─────────────────────────

    [Fact]
    public async Task ListAsync_TwoBoards_ReturnsOnlyTheRequestedBoardsTasks()
    {
        // Arrange
        using var h = new Harness();
        var work = await h.AddBoardAsync("Work");
        await h.CreateOnAsync(h.MainBoardId, "Home task");
        await h.CreateOnAsync(work, "Work task");

        // Act
        var list = await h.AsUser().List(new TaskQuery { BoardId = work }, h.Ct);

        // Assert
        var only = Assert.Single(list);
        Assert.Equal(("Work task", work, "Work"), (only.Title, only.BoardId, only.BoardName));
    }

    [Fact]
    public async Task ListAsync_SearchText_FindsNothingFromAnotherBoard()
    {
        // Arrange
        using var h = new Harness();
        var work = await h.AddBoardAsync("Work");
        await h.CreateOnAsync(work, "needle in the work haystack");

        // Act
        var hits = await h.AsUser().List(new TaskQuery { BoardId = h.MainBoardId, Q = "needle" }, h.Ct);

        // Assert
        Assert.Empty(hits);
    }

    [Fact]
    public async Task ListAsync_BoardByName_IgnoresCase()
    {
        // Arrange
        using var h = new Harness();
        var work = await h.AddBoardAsync("Work");
        await h.CreateOnAsync(work, "Work task");

        // Act
        var list = await h.AsUser().List(new TaskQuery { BoardName = "wORk" }, h.Ct);

        // Assert
        Assert.Single(list);
    }

    [Fact]
    public async Task ListAsync_SeveralBoardsAndNoneNamed_ErrorsListingTheBoards()
    {
        // Arrange
        using var h = new Harness();
        var work = await h.AddBoardAsync("Work");

        // Act
        var ex = await Record.ExceptionAsync(() => h.AsHermes().List(new TaskQuery(), h.Ct));

        // Assert
        var error = Assert.IsType<ValidationException>(ex);
        Assert.Contains($"Main (id {h.MainBoardId})", error.Message);
        Assert.Contains($"Work (id {work})", error.Message);
    }

    [Fact]
    public async Task ListAsync_UnknownBoardName_IsNotFoundAndListsTheBoards()
    {
        // Arrange
        using var h = new Harness();

        // Act
        var ex = await Record.ExceptionAsync(() => h.AsUser().List(new TaskQuery { BoardName = "Nope" }, h.Ct));

        // Assert
        var error = Assert.IsType<NotFoundException>(ex);
        Assert.Contains("Main", error.Message);
    }

    [Fact]
    public async Task CreateAsync_OnlyOneBoard_UsesItWithoutBeingTold()
    {
        // Arrange
        using var h = new Harness();

        // Act
        var created = await h.AsHermes().Create(new CreateTaskRequest("No board given"));

        // Assert
        Assert.Equal("Main", created.Task.Task.BoardName);
    }

    [Fact]
    public async Task CreateAsync_SeveralBoardsAndNoneNamed_ErrorsAndCreatesNothing()
    {
        // Arrange
        using var h = new Harness();
        await h.AddBoardAsync("Work");

        // Act
        var ex = await Record.ExceptionAsync(() => h.AsHermes().Create(new CreateTaskRequest("Where does this go?")));

        // Assert
        Assert.IsType<ValidationException>(ex);
        await using var db = h.NewDb();
        Assert.Equal(0, await db.Tasks.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_ByBoardName_PutsTheTaskOnThatBoard()
    {
        // Arrange
        using var h = new Harness();
        var work = await h.AddBoardAsync("Work");

        // Act
        var created = await h.AsHermes().Create(new CreateTaskRequest("Report", BoardName: "work"));

        // Assert
        Assert.Equal(work, created.Task.Task.BoardId);
        Assert.Equal(work, h.NewDb().Events.Single(e => e.TaskId == created.Task.Task.Id).BoardId);
    }

    [Fact]
    public async Task CreateAsync_SameSourceRefOnTwoBoards_MakesTwoTasks()
    {
        // Arrange
        using var h = new Harness();
        var work = await h.AddBoardAsync("Work");
        var first = await h.AsHermes().Create(new CreateTaskRequest("Mail", Source: TaskSource.Email, SourceRef: "msg-1", BoardId: h.MainBoardId));

        // Act
        var second = await h.AsHermes().Create(new CreateTaskRequest("Mail", Source: TaskSource.Email, SourceRef: "msg-1", BoardId: work));

        // Assert
        Assert.True(second.Created);
        Assert.NotEqual(first.Task.Task.Id, second.Task.Task.Id);
    }

    [Fact]
    public async Task CreateAsync_SameSourceRefOnTheSameBoard_ReturnsTheExistingTask()
    {
        // Arrange
        using var h = new Harness();
        var work = await h.AddBoardAsync("Work");
        var first = await h.AsHermes().Create(new CreateTaskRequest("Mail", Source: TaskSource.Email, SourceRef: "msg-1", BoardId: work));

        // Act
        var again = await h.AsHermes().Create(new CreateTaskRequest("Mail again", Source: TaskSource.Email, SourceRef: "msg-1", BoardId: work));

        // Assert
        Assert.False(again.Created);
        Assert.Equal(first.Task.Task.Id, again.Task.Task.Id);
    }

    [Fact]
    public async Task MoveAsync_BeforeATaskOnAnotherBoard_IsRejected()
    {
        // Arrange
        using var h = new Harness();
        var work = await h.AddBoardAsync("Work");
        var mine = await h.CreateOnAsync(h.MainBoardId, "On Main");
        var foreign = await h.CreateOnAsync(work, "On Work");

        // Act
        var ex = await Record.ExceptionAsync(() => h.AsUser().Move(mine, new MoveTaskRequest(BoardStatus.New, BeforeTaskId: foreign)));

        // Assert
        var error = Assert.IsType<ValidationException>(ex);
        Assert.Contains("another board", error.Message);
    }

    [Fact]
    public async Task MoveAsync_AfterATaskOnAnotherBoard_IsRejected()
    {
        // Arrange
        using var h = new Harness();
        var work = await h.AddBoardAsync("Work");
        var mine = await h.CreateOnAsync(h.MainBoardId, "On Main");
        var foreign = await h.CreateOnAsync(work, "On Work", BoardStatus.Doing);

        // Act
        var ex = await Record.ExceptionAsync(() => h.AsUser().Move(mine, new MoveTaskRequest(BoardStatus.Doing, AfterTaskId: foreign)));

        // Assert
        Assert.IsType<ValidationException>(ex);
        Assert.Equal(BoardStatus.New, (await h.AsUser().GetAsync(mine)).Task.Status);
    }

    [Fact]
    public async Task MoveAsync_ToTheBottomOfALane_OnlyLooksAtItsOwnBoard()
    {
        // Arrange
        using var h = new Harness();
        var work = await h.AddBoardAsync("Work");
        await h.CreateOnAsync(work, "Work A");
        await h.CreateOnAsync(work, "Work B");
        var mine = await h.CreateOnAsync(h.MainBoardId, "Main A");

        // Act
        var moved = await h.AsUser().Move(mine, new MoveTaskRequest(BoardStatus.Waiting));

        // Assert
        Assert.Equal(0, moved.SortOrder);   // first card in an empty lane on Main, whatever Work holds
    }

    [Fact]
    public async Task GetEventsAsync_ForOneBoard_ReturnsOnlyThatBoardsEventsWithBoardNames()
    {
        // Arrange
        using var h = new Harness();
        var work = await h.AddBoardAsync("Work");
        await h.CreateOnAsync(h.MainBoardId, "Home");
        await h.CreateOnAsync(work, "Office");

        // Act
        var feed = await h.AsHermes().GetEventsAsync(0, boardId: work);

        // Assert
        var only = Assert.Single(feed.Events);
        Assert.Equal((work, "Work"), (only.BoardId, only.BoardName));
    }

    [Fact]
    public async Task GetEventsAsync_WithoutABoard_SpansEveryBoard()
    {
        // Arrange
        using var h = new Harness();
        var work = await h.AddBoardAsync("Work");
        await h.CreateOnAsync(h.MainBoardId, "Home");
        await h.CreateOnAsync(work, "Office");

        // Act
        var feed = await h.AsHermes().GetEventsAsync(0);

        // Assert
        Assert.Equal(["Main", "Work"], feed.Events.Select(e => e.BoardName));
    }

    [Fact]
    public async Task GetLatestEventIdAsync_ForABoard_IgnoresEventsOnOtherBoards()
    {
        // Arrange
        using var h = new Harness();
        var work = await h.AddBoardAsync("Work");
        await h.CreateOnAsync(h.MainBoardId, "Home");
        await h.CreateOnAsync(work, "Office");

        // Act
        var onMain = await h.AsUser().GetLatestEventIdAsync(h.MainBoardId);
        var onWork = await h.AsUser().GetLatestEventIdAsync(work);

        // Assert
        Assert.True(onWork > onMain);
        Assert.Equal(1, onMain);
    }

    [Fact]
    public async Task GetInboxAsync_AgentWithTasksOnTwoBoards_SpansBoardsAndNamesThem()
    {
        // Arrange
        using var h = new Harness();
        var work = await h.AddBoardAsync("Work");
        await h.CreateOnAsync(h.MainBoardId, "Triage me on Main");
        await h.CreateOnAsync(work, "Triage me on Work");

        // Act
        var inbox = await h.AsHermes().GetInboxAsync();   // Hermes is the triage agent

        // Assert
        Assert.Equal([("Main", "Triage"), ("Work", "Triage")], inbox.OrderBy(i => i.Task.BoardId).Select(i => (i.Task.BoardName, i.Reason)));
        Assert.All(inbox, i => Assert.True(i.Task.BoardId > 0));
    }

    [Fact]
    public async Task CreateAsync_TaskOnABoard_QueuesTheWebhookRowOnThatBoard()
    {
        // Arrange
        using var h = new Harness();
        var work = await h.AddBoardAsync("Work");

        // Act
        await h.CreateOnAsync(work, "For Hermes", assignee: "Hermes");

        // Assert
        var row = Assert.Single(await h.NewDb().Outbox.ToListAsync());
        Assert.Equal(work, row.BoardId);
    }

    // ───────────────────────── hub messages carry the board ─────────────────────────

    [Fact]
    public async Task TaskService_EveryBroadcast_CarriesTheBoardId()
    {
        // Arrange
        using var h = new Harness();
        var work = await h.AddBoardAsync("Work");
        var id = await h.CreateOnAsync(work, "Chatty", assignee: "Hermes");

        // Act
        await h.AsUser().AddComment(id, new AddCommentRequest("hi"));
        var field = await h.AsUser().UpsertFieldAsync(id, new FieldRequest("Result", "x"));
        await h.AsUser().DeleteFieldAsync(id, field.Id);
        await h.AsHermes().MarkSeen(id, null);
        await h.AsUser().DeleteAsync(id);

        // Assert
        Assert.All(h.Broadcaster.Changed, m => Assert.Equal(work, m.Task.BoardId));
        Assert.All(h.Broadcaster.Comments, m => Assert.Equal(work, m.BoardId));
        Assert.Equal(2, h.Broadcaster.Fields.Count);
        Assert.All(h.Broadcaster.Fields, m => Assert.Equal(work, m.BoardId));
        Assert.All(h.Broadcaster.Seen, m => Assert.Equal(work, m.BoardId));
        Assert.All(h.Broadcaster.Deleted, m => Assert.Equal(work, m.BoardId));
        Assert.NotEmpty(h.Broadcaster.Deleted);
    }
}
