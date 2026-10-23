using DoiQ.Client.Models;

namespace DoiQ.Tests;

public sealed class BoardFilterTests
{
    [Fact]
    public void BoardFilter_LaneOrderSort_AllowsReordering()
    {
        // Arrange
        var filter = new BoardFilter { Sort = SortMode.LaneOrder };

        // Act
        var canReorder = filter.CanReorder;

        // Assert
        Assert.True(canReorder);
    }

    [Theory]
    [InlineData(SortMode.RecentlyUpdated)]
    [InlineData(SortMode.Created)]
    [InlineData(SortMode.DueDate)]
    [InlineData(SortMode.Title)]
    public void BoardFilter_AnyOtherSort_ForbidsReordering(SortMode sort)
    {
        // Arrange
        var filter = new BoardFilter { Sort = sort };

        // Act
        var canReorder = filter.CanReorder;

        // Assert
        Assert.False(canReorder);
    }
}
