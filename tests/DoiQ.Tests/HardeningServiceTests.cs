using DoiQ.Core.Services;
using DoiQ.Shared.Dtos;
using DoiQ.Shared.Enums;

namespace DoiQ.Tests;

/// <summary>Input checks in TaskService: undefined enum values and seen/handled positions beyond the task's last event.</summary>
public sealed class HardeningServiceTests
{
    [Theory]
    [InlineData(999)]
    [InlineData(-1)]
    [InlineData(4)]
    public async Task TaskService_CreateWithUndefinedStatus_ThrowsValidation(int status)
    {
        // Arrange
        using var h = new Harness();

        // Act
        var act = () => h.AsUser().Create(new CreateTaskRequest("Lost", Status: (BoardStatus)status), h.Ct);

        // Assert
        await Assert.ThrowsAsync<ValidationException>(act);
    }

    [Fact]
    public async Task TaskService_CreateWithUndefinedSource_ThrowsValidation()
    {
        // Arrange
        using var h = new Harness();

        // Act
        var act = () => h.AsUser().Create(new CreateTaskRequest("Lost", Source: (TaskSource)42), h.Ct);

        // Assert
        await Assert.ThrowsAsync<ValidationException>(act);
    }

    [Fact]
    public async Task TaskService_MoveToUndefinedStatus_ThrowsValidationAndKeepsTheLane()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "Stay");

        // Act
        var act = () => h.AsUser().Move(id, new MoveTaskRequest((BoardStatus)999), h.Ct);

        // Assert
        await Assert.ThrowsAsync<ValidationException>(act);
        Assert.Equal(BoardStatus.New, (await h.AsUser().GetAsync(id, h.Ct)).Task.Status);
    }

    [Fact]
    public async Task TaskService_ListWithUndefinedStatus_ThrowsValidation()
    {
        // Arrange
        using var h = new Harness();

        // Act
        var act = () => h.AsUser().List(new TaskQuery { Status = (BoardStatus)999 }, h.Ct);

        // Assert
        await Assert.ThrowsAsync<ValidationException>(act);
    }

    [Fact]
    public async Task TaskService_MarkSeenBeyondTheLatestEvent_ClampsToTheLatestEvent()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.Hermes, "Clamp me");
        var latest = (await h.AsUser().GetAsync(id, h.Ct)).LatestEventId;

        // Act
        await h.AsUser().MarkSeen(id, latest + 1000, h.Ct);

        // Assert
        Assert.Equal(latest, (await h.AsUser().GetAsync(id, h.Ct)).SeenThroughEventId);
    }

    [Fact]
    public async Task TaskService_MarkSeenBeyondTheLatestEvent_DoesNotHideLaterActivity()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.Hermes, "Stay unread");
        await h.AsUser().MarkSeen(id, long.MaxValue, h.Ct);

        // Act
        await h.AsHermes().AddComment(id, new AddCommentRequest("new activity"), h.Ct);

        // Assert
        Assert.True((await h.AsUser().GetAsync(id, h.Ct)).Task.Unread);
    }

    [Fact]
    public async Task TaskService_MarkHandledBeyondTheLatestEvent_KeepsTheTaskComingBackWhenTheUserActsAgain()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "Do it", assignee: "Odin");
        await h.AsOdin().MarkHandled(id, 1_000_000, h.Ct);
        Assert.DoesNotContain(await h.AsOdin().GetInboxAsync(h.Ct), i => i.Task.Id == id);

        // Act
        await h.AsUser().AddComment(id, new AddCommentRequest("one more thing"), h.Ct);

        // Assert
        Assert.Contains(await h.AsOdin().GetInboxAsync(h.Ct), i => i.Task.Id == id);
    }

    [Fact]
    public async Task TaskService_MarkSeenWithAnEarlierEvent_StillMovesTheMarkerThere()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.Hermes, "Partly seen");
        await h.AsHermes().AddComment(id, new AddCommentRequest("second"), h.Ct);
        var first = (await h.AsUser().GetAsync(id, h.Ct)).Comments.Single().EventId - 1;

        // Act
        await h.AsUser().MarkSeen(id, first, h.Ct);

        // Assert
        Assert.Equal(first, (await h.AsUser().GetAsync(id, h.Ct)).SeenThroughEventId);
    }
}
