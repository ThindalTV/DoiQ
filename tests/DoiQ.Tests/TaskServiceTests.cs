using DoiQ.Core.Domain;
using DoiQ.Core.Services;
using DoiQ.Shared.Dtos;
using DoiQ.Shared.Enums;
using Microsoft.EntityFrameworkCore;

namespace DoiQ.Tests;

public class MoveTests
{
    [Fact]
    public async Task CreateAsync_TwoTasksInSameLane_ListsNewerOneBelow()
    {
        // Arrange
        using var h = new Harness();
        await h.CreateAsync(h.User, "A");
        await h.CreateAsync(h.User, "B");

        // Act
        var list = await h.AsUser().List(new TaskQuery { Sort = "lane" }, h.Ct);

        // Assert
        Assert.Equal(["A", "B"], list.Select(t => t.Title));   // higher SortOrder first; B is below A
    }

    [Fact]
    public async Task MoveAsync_BeforeTaskId_PlacesCardDirectlyAboveThatCard()
    {
        // Arrange
        using var h = new Harness();
        var a = await h.CreateAsync(h.User, "A");
        var b = await h.CreateAsync(h.User, "B");
        var c = await h.CreateAsync(h.User, "C");   // order on screen: A, B, C

        // Assume
        Assert.Equal(["A", "B", "C"], (await h.AsUser().List(new TaskQuery { Sort = "lane" }, h.Ct)).Select(t => t.Title));

        // Act
        await h.AsUser().Move(c, new MoveTaskRequest(BoardStatus.New, BeforeTaskId: b));

        // Assert
        var list = await h.AsUser().List(new TaskQuery { Sort = "lane" }, h.Ct);
        Assert.Equal(["A", "C", "B"], list.Select(t => t.Title));
        Assert.InRange(list.Single(t => t.Id == c).SortOrder, list.Single(t => t.Id == b).SortOrder, list.Single(t => t.Id == a).SortOrder);
    }

    [Fact]
    public async Task MoveAsync_AfterTaskId_PlacesCardDirectlyBelowThatCard()
    {
        // Arrange
        using var h = new Harness();
        var a = await h.CreateAsync(h.User, "A");
        var b = await h.CreateAsync(h.User, "B");
        var c = await h.CreateAsync(h.User, "C");
        await h.AsUser().Move(c, new MoveTaskRequest(BoardStatus.New, BeforeTaskId: b));   // A, C, B

        // Act
        await h.AsUser().Move(b, new MoveTaskRequest(BoardStatus.New, AfterTaskId: a));

        // Assert
        var list = await h.AsUser().List(new TaskQuery { Sort = "lane" }, h.Ct);
        Assert.Equal(["A", "B", "C"], list.Select(t => t.Title));
    }

    [Fact]
    public async Task MoveAsync_ToDone_SetsDoneAt()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "x");

        // Act
        var done = await h.AsUser().Move(id, new MoveTaskRequest(BoardStatus.Done));

        // Assert
        Assert.Equal(h.Clock.GetUtcNow().UtcDateTime, done.DoneAt);
    }

    [Fact]
    public async Task MoveAsync_OutOfDone_ClearsDoneAt()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "x");
        var done = await h.AsUser().Move(id, new MoveTaskRequest(BoardStatus.Done));

        // Assume
        Assert.NotNull(done.DoneAt);

        // Act
        var back = await h.AsUser().Move(id, new MoveTaskRequest(BoardStatus.Doing));

        // Assert
        Assert.Null(back.DoneAt);
    }

    [Fact]
    public async Task UpdateAsync_StaleExpectedVersion_ThrowsConflict()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "x");
        var staleVersion = (await h.AsUser().GetAsync(id)).Task.Version;
        await h.AsHermes().UpdateAsync(id, new UpdateTaskRequest(Title: "y"));

        // Act
        var ex = await Record.ExceptionAsync(() => h.AsUser().UpdateAsync(id, new UpdateTaskRequest(Title: "z", ExpectedVersion: staleVersion)));

        // Assert
        var stale = Assert.IsType<StaleVersionException>(ex);
        Assert.IsAssignableFrom<ConflictException>(stale);
        Assert.Equal("Hermes", stale.ChangedBy);
    }

    [Fact]
    public async Task MoveAsync_ToWaiting_BroadcastsAfterSaveWithTheEventId()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "x");
        h.Broadcaster.Changed.Clear();

        // Act
        await h.AsUser().Move(id, new MoveTaskRequest(BoardStatus.Waiting));

        // Assert
        var msg = Assert.Single(h.Broadcaster.Changed);
        Assert.Equal(EventType.Moved, msg.Type);
        await using var db = h.NewDb();
        Assert.True(await db.Events.AnyAsync(e => e.Id == msg.EventId && e.Type == EventType.Moved));   // already committed
    }
}

public class PermissionTests
{
    [Fact]
    public async Task DeleteAsync_ByAgent_ThrowsForbidden()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "mine", assignee: "User");

        // Act
        var ex = await Record.ExceptionAsync(() => h.AsHermes().DeleteAsync(id));

        // Assert
        Assert.IsType<ForbiddenException>(ex);
    }

    [Theory]
    [InlineData(BoardStatus.Done)]
    [InlineData(BoardStatus.Doing)]
    public async Task MoveAsync_AgentOnTaskAssignedToUser_ThrowsForbidden(BoardStatus target)
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "mine", assignee: "User");

        // Act
        var ex = await Record.ExceptionAsync(() => h.AsHermes().Move(id, new MoveTaskRequest(target)));

        // Assert
        Assert.IsType<ForbiddenException>(ex);
    }

    [Fact]
    public async Task AddCommentAsync_AgentOnTaskAssignedToUser_Succeeds()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "mine", assignee: "User");

        // Act
        var comment = await h.AsHermes().AddComment(id, new AddCommentRequest("comment instead"));

        // Assert
        Assert.Equal("comment instead", comment.Body);
    }

    [Fact]
    public async Task MoveAsync_AgentFinishesItsOwnTask_ReturnsDone()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "yours", assignee: "Hermes");
        var doing = await h.AsHermes().Move(id, new MoveTaskRequest(BoardStatus.Doing));

        // Assume
        Assert.Equal(BoardStatus.Doing, doing.Status);

        // Act
        var done = await h.AsHermes().Move(id, new MoveTaskRequest(BoardStatus.Done));

        // Assert
        Assert.Equal(BoardStatus.Done, done.Status);
    }

    [Fact]
    public async Task DeleteAsync_ByUser_RemovesTaskAndBroadcastsOnce()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "yours", assignee: "Hermes");

        // Act
        await h.AsUser().DeleteAsync(id);

        // Assert
        await Assert.ThrowsAsync<NotFoundException>(() => h.AsUser().GetAsync(id));
        Assert.Single(h.Broadcaster.Deleted);
    }

    [Fact]
    public async Task UpdateAsync_AgentOnTaskHeldByAnotherAgent_ThrowsForbidden()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "odin's", assignee: "Odin");

        // Act
        var ex = await Record.ExceptionAsync(() => h.AsHermes().UpdateAsync(id, new UpdateTaskRequest(Title: "hijack")));

        // Assert
        Assert.IsType<ForbiddenException>(ex);
    }

    [Fact]
    public async Task UpsertFieldAsync_AgentOnTaskHeldByAnotherAgent_ThrowsForbidden()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "odin's", assignee: "Odin");

        // Act
        var ex = await Record.ExceptionAsync(() => h.AsHermes().UpsertFieldAsync(id, new FieldRequest("Ctx", "x")));

        // Assert
        Assert.IsType<ForbiddenException>(ex);
    }

    [Fact]
    public async Task AddCommentAsync_AgentOnTaskHeldByAnotherAgent_Succeeds()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "odin's", assignee: "Odin");

        // Act
        var comment = await h.AsHermes().AddComment(id, new AddCommentRequest("fyi"));

        // Assert
        Assert.Equal("fyi", comment.Body);
    }

    [Fact]
    public async Task ListAsync_WithoutAuthenticatedActor_ThrowsUnauthorized()
    {
        // Arrange
        using var h = new Harness();
        var svc = new TaskService(h.NewDb(), new TestActor(), new NoopBroadcaster(), new NoopOutboxSignal(), h.Clock,
            Microsoft.Extensions.Options.Options.Create(h.Board), Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskService>.Instance);

        // Act
        var ex = await Record.ExceptionAsync(() => svc.List(new TaskQuery(), h.Ct));

        // Assert
        Assert.IsType<UnauthorizedException>(ex);
    }
}

public class SeenHandledTests
{
    [Fact]
    public async Task GetAsync_AfterOwnWrite_IsNotUnread()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "x");

        // Act
        var detail = await h.AsUser().GetAsync(id);

        // Assert
        Assert.False(detail.Task.Unread);   // own write does not make it unread
    }

    [Fact]
    public async Task GetAsync_AfterAgentComment_IsUnreadAndNeedsTheUser()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "x");
        await h.AsHermes().AddComment(id, new AddCommentRequest("done researching"));

        // Act
        var detail = await h.AsUser().GetAsync(id);

        // Assert
        Assert.True(detail.Task.Unread);
        Assert.True(detail.Task.NeedsMe);
    }

    [Fact]
    public async Task MarkSeenAsync_AfterAgentComment_ClearsUnread()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "x");
        await h.AsHermes().AddComment(id, new AddCommentRequest("done researching"));

        // Assume
        Assert.True((await h.AsUser().GetAsync(id)).Task.Unread);

        // Act
        await h.AsUser().MarkSeen(id, null);

        // Assert
        Assert.False((await h.AsUser().GetAsync(id)).Task.Unread);
    }

    [Fact]
    public async Task AddCommentAsync_ByUser_DoesNotMarkAnythingSeenForAgents()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "x");
        await h.AsHermes().AddComment(id, new AddCommentRequest("done researching"));

        // Act
        await h.AsUser().AddComment(id, new AddCommentRequest("thanks"));

        // Assert
        await using var db = h.NewDb();
        var hermesMarker = await db.ReadMarkers.SingleOrDefaultAsync(m => m.TaskId == id && m.ActorId == h.Hermes.Id);
        Assert.Null(hermesMarker);
    }

    [Fact]
    public async Task UpdateAsync_ByAgent_WritesNoReadMarkers()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "x", assignee: "Hermes");

        // Act
        await h.AsHermes().UpdateAsync(id, new UpdateTaskRequest(Description: "work"));

        // Assert
        await using var db = h.NewDb();
        Assert.Empty(await db.ReadMarkers.ToListAsync());
    }

    [Fact]
    public async Task MarkSeenAsync_WithoutHandling_SetsSeenButNotHandled()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "x", assignee: "Hermes");

        // Act
        await h.AsHermes().MarkSeen(id, null);

        // Assert
        await using var db = h.NewDb();
        var marker = await db.ReadMarkers.SingleAsync();
        Assert.True(marker.SeenThroughEventId > 0);
        Assert.Equal(0, marker.HandledThroughEventId);
    }

    [Fact]
    public async Task MarkHandledAsync_LowerEventIdThanBefore_NeverMovesBackwards()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "x", assignee: "Hermes");
        await h.AsUser().AddComment(id, new AddCommentRequest("one"));
        await h.AsUser().AddComment(id, new AddCommentRequest("two"));
        var latest = (await h.AsHermes().GetAsync(id)).LatestEventId;
        await h.AsHermes().MarkHandled(id, latest);

        // Act
        await h.AsHermes().MarkHandled(id, 2);

        // Assert
        await using var db = h.NewDb();
        Assert.Equal(latest, (await db.ReadMarkers.SingleAsync()).HandledThroughEventId);
    }
}

public class InboxTests
{
    [Fact]
    public async Task GetInboxAsync_NewTaskForTriageAgent_ReturnsTriageEntry()
    {
        // Arrange
        using var h = new Harness();
        await h.CreateAsync(h.User, "Renew domain");

        // Act
        var inbox = await h.AsHermes().GetInboxAsync();

        // Assert
        Assert.Equal("Triage", Assert.Single(inbox).Reason);
    }

    [Fact]
    public async Task GetInboxAsync_NewTaskForNonTriageAgent_IsEmpty()
    {
        // Arrange
        using var h = new Harness();
        await h.CreateAsync(h.User, "Renew domain");

        // Act
        var inbox = await h.AsOdin().GetInboxAsync();

        // Assert
        Assert.Empty(inbox);
    }

    [Fact]
    public async Task GetInboxAsync_TriageTaskMarkedHandled_IsEmpty()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "Renew domain");
        await h.AsHermes().MarkHandled(id, null);

        // Act
        var inbox = await h.AsHermes().GetInboxAsync();

        // Assert
        Assert.Empty(inbox);
    }

    [Fact]
    public async Task GetInboxAsync_HandledTaskAfterUserActs_ReturnsTheTaskAgain()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "Renew domain");
        await h.AsHermes().MarkHandled(id, null);
        await h.AsUser().AddComment(id, new AddCommentRequest("also check the price"));

        // Act
        var inbox = await h.AsHermes().GetInboxAsync();

        // Assert
        Assert.Single(inbox);   // parked task returns when the user acts
    }

    [Fact]
    public async Task GetInboxAsync_TaskAndSubtaskAssignedToAgent_ReturnsBoth()
    {
        // Arrange
        using var h = new Harness();
        var t1 = await h.CreateAsync(h.User, "for hermes", BoardStatus.Waiting, "Hermes");
        var t2 = await h.CreateAsync(h.User, "user task w/ hermes step", BoardStatus.Waiting, "User");
        await h.AsUser().AddSubtask(t2, new AddSubtaskRequest("research", Assignee: "Hermes"));

        // Act
        var inbox = await h.AsHermes().GetInboxAsync();

        // Assert
        Assert.Equal(new[] { t1, t2 }.OrderBy(x => x), inbox.Select(i => i.Task.Id).OrderBy(x => x));
        Assert.Contains(inbox, i => i.Reason == "Subtask");
    }

    [Fact]
    public async Task GetInboxAsync_WorkAssignedToAnotherAgent_IsEmpty()
    {
        // Arrange
        using var h = new Harness();
        var t = await h.CreateAsync(h.User, "for hermes", BoardStatus.Waiting, "Hermes");
        await h.AsUser().AddSubtask(t, new AddSubtaskRequest("research", Assignee: "Hermes"));

        // Act
        var inbox = await h.AsOdin().GetInboxAsync();

        // Assert
        Assert.Empty(inbox);
    }

    [Fact]
    public async Task GetInboxAsync_TaskMovedToDone_IsEmpty()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "t", BoardStatus.Waiting, "Hermes");
        await h.AsHermes().Move(id, new MoveTaskRequest(BoardStatus.Doing));
        await h.AsHermes().Move(id, new MoveTaskRequest(BoardStatus.Done));

        // Act
        var inbox = await h.AsHermes().GetInboxAsync();

        // Assert
        Assert.Empty(inbox);
    }

    [Fact]
    public async Task GetInboxAsync_UserWithAssignedWaitingAndUnreadTasks_ReturnsBoth()
    {
        // Arrange
        using var h = new Harness();
        var waiting = await h.CreateAsync(h.Hermes, "email", BoardStatus.Waiting, "User");
        var other = await h.CreateAsync(h.User, "other");
        await h.AsHermes().AddComment(other, new AddCommentRequest("fyi"));

        // Act
        var queue = await h.AsUser().GetInboxAsync();

        // Assert
        Assert.Equal(2, queue.Count);
        Assert.Contains(queue, i => i.Task.Id == waiting);
        Assert.Contains(queue, i => i.Task.Id == other && i.Reason == "Unread");
    }

    [Fact]
    public async Task ListAsync_NeedsMeFilter_ReturnsTheSameTasksAsTheUsersInbox()
    {
        // Arrange
        using var h = new Harness();
        var waiting = await h.CreateAsync(h.Hermes, "email", BoardStatus.Waiting, "User");
        var other = await h.CreateAsync(h.User, "other");
        await h.AsHermes().AddComment(other, new AddCommentRequest("fyi"));

        // Act
        var needsMe = await h.AsUser().List(new TaskQuery { NeedsMe = true }, h.Ct);

        // Assert
        Assert.Equal(new[] { waiting, other }.OrderBy(x => x), needsMe.Select(t => t.Id).OrderBy(x => x));
    }

    [Fact]
    public async Task GetInboxAsync_UnclaimedPoolTask_ReturnsPoolEntryForTheTriageAgent()
    {
        // Arrange
        using var h = new Harness();
        await h.CreateAsync(h.User, "pool", BoardStatus.Waiting, "any");

        // Act
        var inbox = await h.AsHermes().GetInboxAsync();

        // Assert
        Assert.Single(inbox, i => i.Reason == "Pool");
    }

    [Fact]
    public async Task GetInboxAsync_UnclaimedPoolTask_ReturnsEntryForOtherAgentsToo()
    {
        // Arrange
        using var h = new Harness();
        await h.CreateAsync(h.User, "pool", BoardStatus.Waiting, "any");

        // Act
        var inbox = await h.AsOdin().GetInboxAsync();

        // Assert
        Assert.Single(inbox);
    }

    [Fact]
    public async Task GetInboxAsync_PoolTaskClaimedByAnotherAgent_IsHidden()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "pool", BoardStatus.Waiting, "any");
        await h.AsOdin().ClaimAsync(id);

        // Act
        var inbox = await h.AsHermes().GetInboxAsync();

        // Assert
        Assert.DoesNotContain(inbox, i => i.Task.Id == id);
    }

    [Fact]
    public async Task GetInboxAsync_ClaimLeaseExpired_ReturnsTaskToThePool()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "pool", BoardStatus.Waiting, "any");
        await h.AsOdin().ClaimAsync(id);
        h.Clock.Advance(TimeSpan.FromMinutes(16));

        // Act
        var inbox = await h.AsHermes().GetInboxAsync();

        // Assert
        Assert.Contains(inbox, i => i.Task.Id == id);
    }
}

public class ClaimTests
{
    [Fact]
    public async Task ClaimAsync_TwoAgentsAtOnce_OnlyOneWins()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "pool", BoardStatus.Waiting, "any");

        // Act
        var results = await Task.WhenAll(
            Try(() => h.AsHermes().ClaimAsync(id)),
            Try(() => h.AsOdin().ClaimAsync(id)));

        // Assert
        Assert.Equal(1, results.Count(r => r));
    }

    [Fact]
    public async Task MoveAsync_ClaimHolderToDoing_BecomesAssigneeAndClearsTheClaim()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "pool", BoardStatus.Waiting, "any");
        await h.AsHermes().ClaimAsync(id);

        // Assume
        Assert.Equal("Hermes", (await h.AsUser().GetAsync(id)).Task.ClaimedBy);

        // Act
        var doing = await h.AsHermes().Move(id, new MoveTaskRequest(BoardStatus.Doing));

        // Assert
        Assert.Equal("Hermes", doing.Assignee);
        Assert.False(doing.AssignedToPool);
        Assert.Null(doing.ClaimedBy);
    }

    [Fact]
    public async Task MoveAsync_NonHolderToDoing_ThrowsForbidden()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "pool", BoardStatus.Waiting, "any");
        await h.AsHermes().ClaimAsync(id);

        // Act
        var ex = await Record.ExceptionAsync(() => h.AsOdin().Move(id, new MoveTaskRequest(BoardStatus.Doing)));

        // Assert
        Assert.IsType<ForbiddenException>(ex);
    }

    [Fact]
    public async Task ClaimAsync_ActiveClaimByAnotherAgent_ThrowsConflict()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "pool", BoardStatus.Waiting, "any");
        await h.AsHermes().ClaimAsync(id);

        // Act
        var ex = await Record.ExceptionAsync(() => h.AsOdin().ClaimAsync(id));

        // Assert
        Assert.IsType<ConflictException>(ex);
    }

    [Fact]
    public async Task RenewClaimAsync_BeforeExpiry_ReturnsALeaseExpiry()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "pool", BoardStatus.Waiting, "any");
        await h.AsHermes().ClaimAsync(id);
        h.Clock.Advance(TimeSpan.FromMinutes(10));

        // Act
        var renewed = await h.AsHermes().RenewClaimAsync(id);

        // Assert
        Assert.NotNull(renewed.ClaimExpiresAt);
    }

    [Fact]
    public async Task GetAsync_TwentyMinutesAfterClaimButRenewedAtTen_StillClaimedByTheHolder()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "pool", BoardStatus.Waiting, "any");
        await h.AsHermes().ClaimAsync(id);
        h.Clock.Advance(TimeSpan.FromMinutes(10));
        await h.AsHermes().RenewClaimAsync(id);
        h.Clock.Advance(TimeSpan.FromMinutes(10));

        // Act
        var detail = await h.AsUser().GetAsync(id);

        // Assert
        Assert.Equal("Hermes", detail.Task.ClaimedBy);
    }

    [Fact]
    public async Task ClaimAsync_ExpiredClaim_LetsAnotherAgentTakeOver()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "pool", BoardStatus.Waiting, "any");
        await h.AsHermes().ClaimAsync(id);
        h.Clock.Advance(TimeSpan.FromMinutes(10));
        await h.AsHermes().RenewClaimAsync(id);
        h.Clock.Advance(TimeSpan.FromMinutes(20));   // renewed lease has run out

        // Act
        var taken = await h.AsOdin().ClaimAsync(id);

        // Assert
        Assert.Equal("Odin", taken.ClaimedBy);
    }

    private static async Task<bool> Try(Func<Task> f)
    {
        try { await f(); return true; } catch (ConflictException) { return false; }
    }
}

public class CreateTests
{
    [Fact]
    public async Task CreateAsync_DuplicateSourceRef_ReturnsTheExistingTask()
    {
        // Arrange
        using var h = new Harness();
        var first = await h.AsHermes().Create(new CreateTaskRequest("Mail from bank", Source: TaskSource.Email, SourceRef: "msg-1"));

        // Assume
        Assert.True(first.Created);

        // Act
        var again = await h.AsHermes().Create(new CreateTaskRequest("Mail from bank (dup)", Source: TaskSource.Email, SourceRef: "msg-1"));

        // Assert
        Assert.False(again.Created);
        Assert.Equal(first.Task.Task.Id, again.Task.Task.Id);
    }

    [Fact]
    public async Task CreateAsync_SameRequestKeyTwice_ReturnsTheSameTask()
    {
        // Arrange
        using var h = new Harness();
        var k1 = await h.AsHermes().Create(new CreateTaskRequest("K", RequestKey: "rk"));

        // Act
        var k2 = await h.AsHermes().Create(new CreateTaskRequest("K", RequestKey: "rk"));

        // Assert
        Assert.Equal(k1.Task.Task.Id, k2.Task.Task.Id);
    }

    [Fact]
    public async Task AddCommentAsync_SameRequestKeyTwice_ReturnsTheSameCommentAndStoresOne()
    {
        // Arrange
        using var h = new Harness();
        var taskId = (await h.AsHermes().Create(new CreateTaskRequest("K"))).Task.Task.Id;
        var c1 = await h.AsHermes().AddComment(taskId, new AddCommentRequest("hi", "ck"));

        // Act
        var c2 = await h.AsHermes().AddComment(taskId, new AddCommentRequest("hi", "ck"));

        // Assert
        Assert.Equal(c1.Id, c2.Id);
        Assert.Single(await h.AsUser().GetCommentsAsync(taskId));
    }

    [Fact]
    public async Task UpsertFieldAsync_SameLabelInDifferentCase_UpdatesTheExistingField()
    {
        // Arrange
        using var h = new Harness();
        var created = await h.AsUser().Create(new CreateTaskRequest("t", Fields: [new NewFieldDto("Context", "a")]));
        var id = created.Task.Task.Id;

        // Act
        await h.AsHermes().UpsertFieldAsync(id, new FieldRequest("context", "b"));

        // Assert
        var detail = await h.AsUser().GetAsync(id);
        var field = Assert.Single(detail.Fields);
        Assert.Equal("Context", field.Label);
        Assert.Equal("b", field.Value);
    }

    [Fact]
    public async Task AddFieldAsync_LabelAlreadyUsedIgnoringCase_ThrowsConflict()
    {
        // Arrange
        using var h = new Harness();
        var created = await h.AsUser().Create(new CreateTaskRequest("t", Fields: [new NewFieldDto("Research", "r")]));
        var id = created.Task.Task.Id;

        // Act
        var ex = await Record.ExceptionAsync(() => h.AsUser().AddFieldAsync(id, new FieldRequest("RESEARCH", "dup")));

        // Assert
        Assert.IsType<ConflictException>(ex);
    }
}

public class OutboxTests
{
    [Fact]
    public async Task UpsertFieldAsync_AfterUserCreateAndComment_CoalescesIntoOneDebouncedWakeForTheTriageAgent()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "x");
        await h.AsUser().AddComment(id, new AddCommentRequest("more"));

        // Act
        await h.AsUser().UpsertFieldAsync(id, new FieldRequest("Ctx", "1"));

        // Assert
        await using var db = h.NewDb();
        var row = await db.Outbox.SingleAsync();   // three changes, one webhook
        Assert.Equal(OutboxKind.Todo, row.Kind);
        Assert.Equal(h.Hermes.Id, row.TargetActorId);
        Assert.Equal(h.Clock.GetUtcNow().UtcDateTime.AddSeconds(30), row.NotBefore);
        var last = await db.Events.OrderByDescending(e => e.Id).FirstAsync();
        Assert.Equal(last.Id, row.EventId);
    }

    [Fact]
    public async Task AssignAsync_ToAgent_QueuesOneWakeForThatAgent()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "x", BoardStatus.Waiting);

        // Assume
        await using (var before = h.NewDb())
            Assert.Empty(await before.Outbox.ToListAsync());

        // Act
        await h.AsUser().AssignAsync(id, new AssignRequest("Odin"));

        // Assert
        await using var db = h.NewDb();
        Assert.Equal(h.Odin.Id, (await db.Outbox.SingleAsync()).TargetActorId);
    }

    [Fact]
    public async Task AssignAsync_ToPool_QueuesAWakeForEveryAgent()
    {
        // Arrange
        using var h = new Harness();
        var pool = await h.CreateAsync(h.User, "p", BoardStatus.Waiting);

        // Act
        await h.AsUser().AssignAsync(pool, new AssignRequest("any"));

        // Assert
        await using var db = h.NewDb();
        Assert.Equal(2, await db.Outbox.CountAsync(o => o.TaskId == pool));
    }

    [Fact]
    public async Task AddCommentAsync_ByAgentOnTaskItAssignedToTheUser_QueuesOnlyTheAssignmentNotify()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.Hermes, "needs user", BoardStatus.Waiting, "User");

        // Act
        await h.AsHermes().AddComment(id, new AddCommentRequest("fyi"));

        // Assert
        await using var db = h.NewDb();
        var row = await db.Outbox.SingleAsync();
        Assert.Equal(OutboxKind.Notify, row.Kind);
        Assert.Equal("Hermes assigned you: needs user", row.PayloadJson);
    }

    [Fact]
    public async Task CreateAsync_AssignedToAgent_WritesTheOutboxRowInTheSameSaveAsTheEvent()
    {
        // Arrange
        using var h = new Harness();

        // Act
        var id = await h.CreateAsync(h.User, "x", BoardStatus.Waiting, "Hermes");

        // Assert
        await using var db = h.NewDb();
        var ev = await db.Events.SingleAsync(e => e.Type == EventType.Created);
        var row = await db.Outbox.SingleAsync();
        Assert.Equal(ev.Id, row.EventId);
        Assert.Equal(id, row.TaskId);
    }
}

public class QueryTests
{
    private static async Task<(int Old, int Fresh)> ArrangeOldAndFreshDoneAsync(Harness h)
    {
        var old = await h.CreateAsync(h.User, "old");
        var fresh = await h.CreateAsync(h.User, "fresh");
        await h.AsUser().Move(old, new MoveTaskRequest(BoardStatus.Done));
        h.Clock.Advance(TimeSpan.FromDays(10));
        await h.AsUser().Move(fresh, new MoveTaskRequest(BoardStatus.Done));
        h.Clock.Advance(TimeSpan.FromDays(5));   // old: 15 days, fresh: 5 days
        return (old, fresh);
    }

    private static async Task ArrangeAssigneeMixAsync(Harness h)
    {
        await h.CreateAsync(h.User, "b", assignee: "Hermes");
        await h.CreateAsync(h.User, "a", assignee: "User");
        await h.CreateAsync(h.User, "c");
    }

    [Fact]
    public async Task ListAsync_DefaultQuery_HidesDoneTasksOlderThan14Days()
    {
        // Arrange
        using var h = new Harness();
        await ArrangeOldAndFreshDoneAsync(h);

        // Act
        var visible = await h.AsUser().List(new TaskQuery(), h.Ct);

        // Assert
        Assert.Equal(["fresh"], visible.Select(t => t.Title));
    }

    [Fact]
    public async Task ListAsync_IncludeArchived_ReturnsOldDoneTasksToo()
    {
        // Arrange
        using var h = new Harness();
        await ArrangeOldAndFreshDoneAsync(h);

        // Act
        var all = await h.AsUser().List(new TaskQuery { IncludeArchived = true }, h.Ct);

        // Assert
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task GetAsync_ArchivedDoneTask_StillReturnsIt()
    {
        // Arrange
        using var h = new Harness();
        var (old, _) = await ArrangeOldAndFreshDoneAsync(h);

        // Act
        var detail = await h.AsUser().GetAsync(old);

        // Assert
        Assert.Equal("old", detail.Task.Title);
    }

    [Fact]
    public async Task ListAsync_SearchTerm_MatchesTitleDescriptionFieldsAndCommentsIgnoringCase()
    {
        // Arrange
        using var h = new Harness();
        var a = await h.CreateAsync(h.User, "Alpha");
        var b = await h.CreateAsync(h.User, "Beta");
        var c = await h.CreateAsync(h.User, "Gamma");
        await h.AsUser().UpdateAsync(b, new UpdateTaskRequest(Description: "contains NEEDLE here"));
        await h.AsUser().UpsertFieldAsync(c, new FieldRequest("Notes", "a needle in a field"));
        await h.AsUser().AddComment(a, new AddCommentRequest("Needle in comment"));

        // Act
        var hits = await h.AsUser().List(new TaskQuery { Q = "needle" }, h.Ct);

        // Assert
        Assert.Equal(3, hits.Count);
    }

    [Fact]
    public async Task ListAsync_SearchTermWithPercentSign_IsMatchedLiterally()
    {
        // Arrange
        using var h = new Harness();
        await h.CreateAsync(h.User, "Alpha");

        // Act
        var hits = await h.AsUser().List(new TaskQuery { Q = "100%" }, h.Ct);

        // Assert
        Assert.Empty(hits);
    }

    [Fact]
    public async Task ListAsync_SortByTitle_OrdersAlphabetically()
    {
        // Arrange
        using var h = new Harness();
        await ArrangeAssigneeMixAsync(h);

        // Act
        var list = await h.AsUser().List(new TaskQuery { Sort = "title" }, h.Ct);

        // Assert
        Assert.Equal(["a", "b", "c"], list.Select(t => t.Title));
    }

    [Fact]
    public async Task ListAsync_AssigneeAgents_ReturnsOnlyAgentTasks()
    {
        // Arrange
        using var h = new Harness();
        await ArrangeAssigneeMixAsync(h);

        // Act
        var list = await h.AsUser().List(new TaskQuery { Assignee = "agents" }, h.Ct);

        // Assert
        Assert.Equal(["b"], list.Select(t => t.Title));
    }

    [Fact]
    public async Task ListAsync_AssigneeMe_ReturnsOnlyTheCallersTasks()
    {
        // Arrange
        using var h = new Harness();
        await ArrangeAssigneeMixAsync(h);

        // Act
        var list = await h.AsUser().List(new TaskQuery { Assignee = "me" }, h.Ct);

        // Assert
        Assert.Equal(["a"], list.Select(t => t.Title));
    }

    [Fact]
    public async Task ListAsync_AssigneeNone_ReturnsOnlyUnassignedTasks()
    {
        // Arrange
        using var h = new Harness();
        await ArrangeAssigneeMixAsync(h);

        // Act
        var list = await h.AsUser().List(new TaskQuery { Assignee = "none" }, h.Ct);

        // Assert
        Assert.Equal(["c"], list.Select(t => t.Title));
    }

    private static async Task ArrangeThreeEventsAsync(Harness h)
    {
        var id = await h.CreateAsync(h.User, "x");
        await h.AsHermes().AddComment(id, new AddCommentRequest("mine"));
        await h.AsUser().AddComment(id, new AddCommentRequest("yours"));
    }

    [Fact]
    public async Task GetLatestEventIdAsync_NoEvents_ReturnsZero()
    {
        // Arrange
        using var h = new Harness();

        // Act
        var latest = await h.AsUser().GetLatestEventIdAsync();

        // Assert
        Assert.Equal(0, latest);
    }

    [Fact]
    public async Task GetLatestEventIdAsync_AfterThreeEvents_ReturnsTheLastEventId()
    {
        // Arrange
        using var h = new Harness();
        await ArrangeThreeEventsAsync(h);
        var all = await h.AsUser().GetEventsAsync(0, 10);

        // Act
        var latest = await h.AsUser().GetLatestEventIdAsync();

        // Assert
        Assert.Equal(all.Events[^1].Id, latest);
    }

    [Fact]
    public async Task GetEventsAsync_LimitTwo_ReturnsFirstPageWithMore()
    {
        // Arrange
        using var h = new Harness();
        await ArrangeThreeEventsAsync(h);

        // Act
        var page = await h.AsUser().GetEventsAsync(0, 2);

        // Assert
        Assert.Equal(2, page.Events.Count);
        Assert.True(page.HasMore);
    }

    [Fact]
    public async Task GetEventsAsync_FromNextCursor_ReturnsTheRestWithoutMore()
    {
        // Arrange
        using var h = new Harness();
        await ArrangeThreeEventsAsync(h);
        var firstPage = await h.AsUser().GetEventsAsync(0, 2);

        // Act
        var rest = await h.AsUser().GetEventsAsync(firstPage.NextCursor, 10);

        // Assert
        Assert.False(rest.HasMore);
        Assert.Single(rest.Events);
    }

    [Fact]
    public async Task GetEventsAsync_ByAgent_HidesItsOwnEvents()
    {
        // Arrange
        using var h = new Harness();
        await ArrangeThreeEventsAsync(h);

        // Act
        var feed = await h.AsHermes().GetEventsAsync(0, 10);

        // Assert
        Assert.DoesNotContain(feed.Events, e => e.ActorId == h.Hermes.Id);
        Assert.Equal(2, feed.Events.Count);
    }

    [Fact]
    public async Task AddSubtaskAsync_WithBeforeId_InsertsAboveThatSubtask()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "x");
        await h.AsUser().AddSubtask(id, new AddSubtaskRequest("one"));
        var two = await h.AsUser().AddSubtask(id, new AddSubtaskRequest("two"));

        // Act
        await h.AsUser().AddSubtask(id, new AddSubtaskRequest("three", BeforeId: two.Id));

        // Assert
        var detail = await h.AsUser().GetAsync(id);
        Assert.Equal(["one", "three", "two"], detail.Subtasks.Select(s => s.Title));
    }

    [Fact]
    public async Task UpdateSubtaskAsync_MarkDone_UpdatesProgressAndDoneAt()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "x");
        var one = await h.AsUser().AddSubtask(id, new AddSubtaskRequest("one"));
        await h.AsUser().AddSubtask(id, new AddSubtaskRequest("two"));
        await h.AsUser().AddSubtask(id, new AddSubtaskRequest("three"));

        // Act
        await h.AsUser().UpdateSubtaskAsync(id, one.Id, new UpdateSubtaskRequest(Done: true));

        // Assert
        var detail = await h.AsUser().GetAsync(id);
        Assert.Equal(3, detail.Task.SubtaskTotal);
        Assert.Equal(1, detail.Task.SubtaskDone);
        Assert.NotNull(detail.Subtasks.Single(s => s.Id == one.Id).DoneAt);
    }
}
