using DoiQ.Client.Models;
using DoiQ.Client.Services;

namespace DoiQ.Tests;

public sealed class BoardStateTests
{
    private static TaskVm NewTask(int id, BoardLane lane = BoardLane.New, double order = 0, long version = 1) =>
        new(id, $"Task {id}", "", lane, AssigneeVm.Unassigned, null, false, false, false, "User", DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch, "User", [], [], [], version, order);

    private static CommentVm Comment(int id) => new(id, "Hermes", "hi", DateTimeOffset.UnixEpoch.AddHours(1), IsNew: true);

    [Fact]
    public void BoardState_Reset_OrdersLanesThenHighestSortOrderFirst()
    {
        // Arrange
        var state = new BoardState();

        // Act
        state.Reset([NewTask(1, BoardLane.Doing, 5), NewTask(2, BoardLane.New, 1), NewTask(3, BoardLane.New, 9)], baselineEventId: 7);

        // Assert
        Assert.Equal([3, 2, 1], state.Tasks.Select(t => t.Id));
        Assert.Equal(7, state.LastEventId);
    }

    [Fact]
    public void BoardState_UpsertWithOlderVersion_KeepsTheNewerTask()
    {
        // Arrange
        var state = new BoardState();
        state.Reset([NewTask(1, BoardLane.Doing, version: 5)], 0);

        // Act
        var applied = state.Upsert(NewTask(1, BoardLane.New, version: 4));

        // Assert
        Assert.False(applied);
        Assert.Equal(BoardLane.Doing, state.Find(1)!.Lane);
    }

    [Fact]
    public void BoardState_UpsertWithSameVersion_ReplacesAndReorders()
    {
        // Arrange
        var state = new BoardState();
        state.Reset([NewTask(1, order: 1), NewTask(2, order: 2)], 0);

        // Act
        state.Upsert(NewTask(1, order: 10));

        // Assert
        Assert.Equal([1, 2], state.Tasks.Select(t => t.Id));
    }

    [Fact]
    public void BoardState_TryAdvance_AcceptsOnlyNewerEventIds()
    {
        // Arrange
        var state = new BoardState();
        state.Reset([], 10);

        // Act
        var results = new[] { state.TryAdvance(10), state.TryAdvance(9), state.TryAdvance(11) };

        // Assert
        Assert.Equal([false, false, true], results);
        Assert.Equal(11, state.LastEventId);
    }

    [Fact]
    public void BoardState_TryAdvanceInOrder_MovesTheCursorEachTime()
    {
        // Arrange
        var state = new BoardState();
        state.Reset([], 10);

        // Act
        state.TryAdvance(11);
        state.TryAdvance(12);

        // Assert
        Assert.Equal(12, state.LastEventId);
        Assert.False(state.HasGap);
    }

    [Fact]
    public void BoardState_TryAdvanceWithTheLaterEventFirst_HoldsTheCursorUntilTheEarlierOneArrives()
    {
        // Arrange
        var state = new BoardState();
        state.Reset([], 10);

        // Act
        state.TryAdvance(12);
        var heldAt = state.LastEventId;
        var gapWhileHeld = state.HasGap;
        state.TryAdvance(11);

        // Assert
        Assert.Equal(10, heldAt);
        Assert.True(gapWhileHeld);
        Assert.Equal(12, state.LastEventId);
        Assert.False(state.HasGap);
    }

    [Fact]
    public void BoardState_TryAdvanceWithAnEventTwice_RefusesTheSecondEvenWhileItWaitsAboveTheCursor()
    {
        // Arrange
        var state = new BoardState();
        state.Reset([], 10);

        // Act
        var results = new[] { state.TryAdvance(13), state.TryAdvance(13), state.TryAdvance(11), state.TryAdvance(11) };

        // Assert
        Assert.Equal([true, false, true, false], results);
    }

    [Fact]
    public void BoardState_CatchUpToCoveringTheHole_FillsTheGapAndAdvancesPastTheEarlyEvents()
    {
        // Arrange
        var state = new BoardState();
        state.Reset([], 10);
        state.TryAdvance(15);

        // Act
        state.CatchUpTo(14);

        // Assert
        Assert.Equal(15, state.LastEventId);
        Assert.False(state.HasGap);
        Assert.True(state.IsApplied(12));
        Assert.False(state.IsApplied(16));
    }

    [Fact]
    public void BoardState_CatchUpToBelowTheCursor_NeverMovesItBack()
    {
        // Arrange
        var state = new BoardState();
        state.Reset([], 10);

        // Act
        state.CatchUpTo(4);

        // Assert
        Assert.Equal(10, state.LastEventId);
    }

    [Fact]
    public void BoardState_AddCommentTwice_AddsItOnceAndMarksTheTaskUnread()
    {
        // Arrange
        var state = new BoardState();
        state.Reset([NewTask(1)], 0);

        // Act
        var first = state.AddComment(1, Comment(5), unread: true);
        var second = state.AddComment(1, Comment(5), unread: true);

        // Assert
        Assert.True(first);
        Assert.False(second);
        Assert.Single(state.Find(1)!.Comments);
        Assert.True(state.Find(1)!.Unread);
    }

    [Fact]
    public void BoardState_ApplyFieldWithNull_RemovesTheField()
    {
        // Arrange
        var state = new BoardState();
        state.Reset([NewTask(1)], 0);
        state.ApplyField(1, 3, new InfoFieldVm(3, "Notes", "x", "Hermes", DateTimeOffset.UnixEpoch, true), unread: false);

        // Act
        state.ApplyField(1, 3, null, unread: false);

        // Assert
        Assert.Empty(state.Find(1)!.Fields);
    }

    [Fact]
    public void BoardState_RemoveUnknownTask_ReturnsFalse()
    {
        // Arrange
        var state = new BoardState();
        state.Reset([NewTask(1)], 0);

        // Act
        var removed = state.Remove(99);

        // Assert
        Assert.False(removed);
        Assert.Single(state.Tasks);
    }
}
