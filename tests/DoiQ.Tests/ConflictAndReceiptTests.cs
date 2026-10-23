using DoiQ.Core.Services;
using DoiQ.Shared.Dtos;
using DoiQ.Shared.Enums;

namespace DoiQ.Tests;

public class StaleWriteTests
{
    [Fact]
    public async Task MoveAsync_StaleExpectedVersion_ThrowsStaleVersionNamingWhoChangedIt()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "x");
        var seen = (await h.AsUser().GetAsync(id)).Task.Version;
        await h.AsHermes().UpdateAsync(id, new UpdateTaskRequest(Title: "y"));

        // Act
        var ex = await Record.ExceptionAsync(() => h.AsUser().Move(id, new MoveTaskRequest(BoardStatus.Doing, ExpectedVersion: seen)));

        // Assert
        var stale = Assert.IsType<StaleVersionException>(ex);
        Assert.Equal("Hermes", stale.ChangedBy);
        Assert.Equal(seen + 1, stale.CurrentVersion);
    }

    [Fact]
    public async Task AssignAsync_StaleExpectedVersion_ThrowsStaleVersion()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "x");
        var seen = (await h.AsUser().GetAsync(id)).Task.Version;
        await h.AsHermes().UpdateAsync(id, new UpdateTaskRequest(Title: "y"));

        // Act
        var ex = await Record.ExceptionAsync(() => h.AsUser().AssignAsync(id, new AssignRequest("Odin", seen)));

        // Assert
        Assert.IsType<StaleVersionException>(ex);
    }

    [Fact]
    public async Task UpdateAsync_StaleExpectedVersion_ChangesNothing()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "x");
        var seen = (await h.AsUser().GetAsync(id)).Task.Version;
        await h.AsHermes().UpdateAsync(id, new UpdateTaskRequest(Title: "y"));

        // Act
        await Record.ExceptionAsync(() => h.AsUser().UpdateAsync(id, new UpdateTaskRequest(Title: "z", ExpectedVersion: seen)));

        // Assert
        Assert.Equal("y", (await h.AsUser().GetAsync(id)).Task.Title);
    }
}

public class CommentMarkerTests
{
    [Fact]
    public async Task GetAsync_AgentCommentNotYetSeen_IsFlaggedUnreadWithItsEventId()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "x");
        await h.AsHermes().AddComment(id, new AddCommentRequest("hello"));

        // Act
        var detail = await h.AsUser().GetAsync(id);

        // Assert
        var comment = Assert.Single(detail.Comments);
        Assert.True(comment.Unread);
        Assert.True(comment.EventId > 0);
    }

    [Fact]
    public async Task GetAsync_CommentsAfterTheSeenMarker_AreTheOnlyUnreadOnes()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "x");
        await h.AsHermes().AddComment(id, new AddCommentRequest("first"));
        await h.AsUser().MarkSeen(id, null);
        await h.AsHermes().AddComment(id, new AddCommentRequest("second"));

        // Act
        var detail = await h.AsUser().GetAsync(id);

        // Assert
        Assert.Equal([false, true], detail.Comments.Select(c => c.Unread));
    }

    [Fact]
    public async Task GetAsync_UsersOwnComment_IsNeverUnreadForTheUser()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "x");
        await h.AsUser().AddComment(id, new AddCommentRequest("mine"));

        // Act
        var detail = await h.AsUser().GetAsync(id);

        // Assert
        Assert.False(Assert.Single(detail.Comments).Unread);
    }

    [Fact]
    public async Task GetAsync_AfterMarkSeen_ReportsTheCallersSeenPosition()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "x");
        await h.AsHermes().AddComment(id, new AddCommentRequest("hello"));
        await h.AsUser().MarkSeen(id, null);

        // Act
        var detail = await h.AsUser().GetAsync(id);

        // Assert
        Assert.Equal(detail.LatestEventId, detail.SeenThroughEventId);
    }

    [Fact]
    public async Task GetCommentsAsync_CarriesEventIdAndUnread()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "x");
        await h.AsHermes().AddComment(id, new AddCommentRequest("hello"));

        // Act
        var comments = await h.AsUser().GetCommentsAsync(id);

        // Assert
        var comment = Assert.Single(comments);
        Assert.True(comment.EventId > 0);
        Assert.True(comment.Unread);
    }

    [Fact]
    public async Task AddCommentAsync_Result_CarriesTheEventIdOfTheBroadcast()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "x");

        // Act
        var dto = await h.AsUser().AddComment(id, new AddCommentRequest("hello"));

        // Assert
        Assert.Equal(h.Broadcaster.Comments.Single().EventId, dto.EventId);
    }
}

public class ReadReceiptTests
{
    [Fact]
    public async Task GetAsync_AgentHasSeenTheTask_ListsItInSeenByPastTheUsersComment()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "x");
        var mine = await h.AsUser().AddComment(id, new AddCommentRequest("please look"));
        await h.AsHermes().MarkSeen(id, null);

        // Act
        var detail = await h.AsUser().GetAsync(id);

        // Assert
        var seenBy = Assert.Single(detail.SeenBy!);
        Assert.Equal("Hermes", seenBy.Actor);
        Assert.True(seenBy.SeenThroughEventId >= mine.EventId);
    }

    [Fact]
    public async Task GetAsync_AgentSawOnlyAnEarlierEvent_SeenByPositionIsBehindTheLaterComment()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "x");
        await h.AsHermes().MarkSeen(id, null);
        var later = await h.AsUser().AddComment(id, new AddCommentRequest("after you looked"));

        // Act
        var detail = await h.AsUser().GetAsync(id);

        // Assert
        Assert.True(Assert.Single(detail.SeenBy!).SeenThroughEventId < later.EventId);
    }

    [Fact]
    public async Task GetAsync_NobodyElseHasSeenIt_SeenByIsEmpty()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "x");

        // Act
        var detail = await h.AsUser().GetAsync(id);

        // Assert
        Assert.Empty(detail.SeenBy!);
    }

    [Fact]
    public async Task GetAsync_ForTheAgentThatSawIt_SeenByLeavesOutTheCaller()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "x");
        await h.AsHermes().MarkSeen(id, null);

        // Act
        var detail = await h.AsHermes().GetAsync(id);

        // Assert
        Assert.Empty(detail.SeenBy!);
    }

    [Fact]
    public async Task MarkSeenAsync_ByAgent_BroadcastsTheNewPosition()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "x");

        // Act
        await h.AsHermes().MarkSeen(id, null);

        // Assert
        var msg = Assert.Single(h.Broadcaster.Seen);
        Assert.Equal((id, h.Hermes.Id, "Hermes"), (msg.TaskId, msg.ActorId, msg.Actor));
        Assert.True(msg.SeenThroughEventId > 0);
    }

    [Fact]
    public async Task MarkSeenAsync_NothingNewSinceLastTime_BroadcastsNothing()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "x");
        await h.AsHermes().MarkSeen(id, null);
        h.Broadcaster.Seen.Clear();

        // Act
        await h.AsHermes().MarkSeen(id, null);

        // Assert
        Assert.Empty(h.Broadcaster.Seen);
    }

    [Fact]
    public async Task MarkHandledAsync_ByAgent_BroadcastsNoReceipt()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "x");

        // Act
        await h.AsHermes().MarkHandled(id, null);

        // Assert
        Assert.Empty(h.Broadcaster.Seen);
    }
}
