using DoiQ.Core.Services;
using DoiQ.Shared.Dtos;
using DoiQ.Shared.Enums;
using Microsoft.EntityFrameworkCore;

namespace DoiQ.Tests;

/// <summary>Moving a task to another board: what goes with it, who may do it, and what the boards and feeds see.</summary>
public sealed class MoveToBoardTests : IDisposable
{
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromMinutes(1));

    private CancellationToken Ct => _cts.Token;

    public void Dispose() => _cts.Dispose();

    [Fact]
    public async Task MoveToBoard_ByUser_PutsTaskOnTargetBoardInSameLane()
    {
        // Arrange
        using var h = new Harness();
        var work = await h.AddBoardAsync("Work");
        var id = await h.CreateOnAsync(h.MainBoardId, "Plan trip", BoardStatus.Doing);

        // Act
        var moved = await h.AsUser().MoveToBoard(id, new MoveToBoardRequest(work), Ct);

        // Assert
        Assert.Equal(work, moved.BoardId);
        Assert.Equal("Work", moved.BoardName);
        Assert.Equal(BoardStatus.Doing, moved.Status);
        Assert.Empty((await h.AsUser().LoadBoard(h.MainBoardId, Ct)).Tasks);
        Assert.Equal(id, Assert.Single((await h.AsUser().LoadBoard(work, Ct)).Tasks).Id);
    }

    [Fact]
    public async Task MoveToBoard_TaskWithCommentsFieldsAndSubtasks_KeepsThem()
    {
        // Arrange
        using var h = new Harness();
        var work = await h.AddBoardAsync("Work");
        var id = await h.CreateOnAsync(h.MainBoardId, "Plan trip");
        await h.AsUser().AddComment(id, new AddCommentRequest("Booked?"));
        await h.AsUser().AddFieldAsync(id, new FieldRequest("Hotel", "Ritz"));
        await h.AsUser().AddSubtask(id, new AddSubtaskRequest("Pack"));

        // Act
        await h.AsUser().MoveToBoard(id, new MoveToBoardRequest(work), Ct);

        // Assert
        var detail = await h.AsUser().GetAsync(id, Ct);
        Assert.Single(detail.Comments);
        Assert.Single(detail.Fields);
        Assert.Single(detail.Subtasks);
    }

    [Fact]
    public async Task MoveToBoard_ByBoardName_FindsTheBoardIgnoringCase()
    {
        // Arrange
        using var h = new Harness();
        var work = await h.AddBoardAsync("Work");
        var id = await h.CreateOnAsync(h.MainBoardId, "Plan trip");

        // Act
        var moved = await h.AsUser().MoveToBoard(id, new MoveToBoardRequest(BoardName: "work"), Ct);

        // Assert
        Assert.Equal(work, moved.BoardId);
    }

    [Fact]
    public async Task MoveToBoard_GoesToBottomOfItsLaneOnTargetBoard()
    {
        // Arrange
        using var h = new Harness();
        var work = await h.AddBoardAsync("Work");
        var there = await h.CreateOnAsync(work, "Already there");
        var id = await h.CreateOnAsync(h.MainBoardId, "Arriving");

        // Act
        var moved = await h.AsUser().MoveToBoard(id, new MoveToBoardRequest(work), Ct);

        // Assert
        var other = (await h.AsUser().LoadBoard(work, Ct)).Tasks.Single(t => t.Id == there);
        Assert.True(moved.SortOrder < other.SortOrder);
    }

    [Fact]
    public async Task MoveToBoard_RecordsMovedToBoardEventOnNewBoardAndBroadcastsIt()
    {
        // Arrange
        using var h = new Harness();
        var work = await h.AddBoardAsync("Work");
        var id = await h.CreateOnAsync(h.MainBoardId, "Plan trip");

        // Act
        await h.AsUser().MoveToBoard(id, new MoveToBoardRequest(work), Ct);

        // Assert
        var ev = (await h.AsHermes().GetEventsAsync(0, 100, work, Ct)).Events.Single(e => e.TaskId == id);
        Assert.Equal(EventType.MovedToBoard, ev.Type);
        Assert.Contains("\"from\":\"Main\"", ev.Payload);
        var pushed = h.Broadcaster.Changed[^1];
        Assert.Equal(EventType.MovedToBoard, pushed.Type);
        Assert.Equal(work, pushed.Task.BoardId);
    }

    [Fact]
    public async Task MoveToBoard_SameBoard_ChangesNothing()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateOnAsync(h.MainBoardId, "Plan trip");
        var before = await h.AsUser().GetAsync(id, Ct);

        // Act
        var after = await h.AsUser().MoveToBoard(id, new MoveToBoardRequest(h.MainBoardId), Ct);

        // Assert
        Assert.Equal(before.Task.Version, after.Version);
    }

    [Fact]
    public async Task MoveToBoard_NoBoardGiven_IsRejected()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateOnAsync(h.MainBoardId, "Plan trip");

        // Act
        var ex = await Record.ExceptionAsync(() => h.AsUser().MoveToBoard(id, new MoveToBoardRequest(), Ct));

        // Assert
        Assert.IsType<ValidationException>(ex);
    }

    [Fact]
    public async Task MoveToBoard_UnknownBoard_IsNotFound()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateOnAsync(h.MainBoardId, "Plan trip");

        // Act
        var ex = await Record.ExceptionAsync(() => h.AsUser().MoveToBoard(id, new MoveToBoardRequest(BoardId: 999), Ct));

        // Assert
        Assert.IsType<NotFoundException>(ex);
    }

    [Fact]
    public async Task MoveToBoard_StaleExpectedVersion_IsAConflict()
    {
        // Arrange
        using var h = new Harness();
        var work = await h.AddBoardAsync("Work");
        var id = await h.CreateOnAsync(h.MainBoardId, "Plan trip");

        // Act
        var ex = await Record.ExceptionAsync(() => h.AsUser().MoveToBoard(id, new MoveToBoardRequest(work, ExpectedVersion: 99), Ct));

        // Assert
        Assert.IsType<StaleVersionException>(ex);
    }

    [Fact]
    public async Task MoveToBoard_TargetHasTaskFromSameSource_IsAConflict()
    {
        // Arrange
        using var h = new Harness();
        var work = await h.AddBoardAsync("Work");
        await h.AsHermes().Create(new CreateTaskRequest("Mail", Source: TaskSource.Email, SourceRef: "msg-1", BoardId: work), Ct);
        var id = (await h.AsHermes().Create(new CreateTaskRequest("Mail", Source: TaskSource.Email, SourceRef: "msg-1", BoardId: h.MainBoardId), Ct)).Task.Task.Id;

        // Act
        var ex = await Record.ExceptionAsync(() => h.AsUser().MoveToBoard(id, new MoveToBoardRequest(work), Ct));

        // Assert
        Assert.IsType<ConflictException>(ex);
    }

    [Fact]
    public async Task MoveToBoard_ByAgentOnItsOwnTask_Succeeds()
    {
        // Arrange
        using var h = new Harness();
        var work = await h.AddBoardAsync("Work");
        var id = await h.CreateOnAsync(h.MainBoardId, "Sort mail", assignee: "Hermes");

        // Act
        var moved = await h.AsHermes().MoveToBoard(id, new MoveToBoardRequest(BoardName: "Work"), Ct);

        // Assert
        Assert.Equal(work, moved.BoardId);
    }

    [Fact]
    public async Task MoveToBoard_ByAgentOnTaskAnotherAgentHolds_IsForbidden()
    {
        // Arrange
        using var h = new Harness();
        var work = await h.AddBoardAsync("Work");
        var id = await h.CreateOnAsync(h.MainBoardId, "Odin's job", assignee: "Odin");

        // Act
        var ex = await Record.ExceptionAsync(() => h.AsHermes().MoveToBoard(id, new MoveToBoardRequest(work), Ct));

        // Assert
        Assert.IsType<ForbiddenException>(ex);
    }

    [Fact]
    public async Task MoveToBoard_ByUser_WakesAssignedAgentAndQueuedWebhooksFollowTheTask()
    {
        // Arrange
        using var h = new Harness();
        var work = await h.AddBoardAsync("Work");
        var id = await h.CreateOnAsync(h.MainBoardId, "Sort mail", assignee: "Hermes");

        // Act
        await h.AsUser().MoveToBoard(id, new MoveToBoardRequest(work), Ct);

        // Assert
        var rows = await h.NewDb().Outbox.AsNoTracking().Where(o => o.TaskId == id && o.SentAt == null).ToListAsync(Ct);
        Assert.NotEmpty(rows);
        Assert.All(rows, o => Assert.Equal(work, o.BoardId));
        Assert.Contains(rows, o => o.TargetActorId == h.Hermes.Id);
    }
}
