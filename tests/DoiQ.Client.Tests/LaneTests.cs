using Bunit;
using DoiQ.Client.Components;
using DoiQ.Client.Models;
using Microsoft.AspNetCore.Components.Web;

namespace DoiQ.Client.Tests;

public sealed class LaneTests : ClientTestBase
{
    private IRenderedComponent<Lane> RenderLane(IReadOnlyList<TaskVm> tasks, Action<ComponentParameterCollectionBuilder<Lane>>? more = null) =>
        Render<Lane>(p =>
        {
            p.Add(l => l.LaneId, BoardLane.Waiting);
            p.Add(l => l.Title, "Waiting");
            p.Add(l => l.Tasks, tasks);
            more?.Invoke(p);
        });

    [Fact]
    public void Lane_WithTasks_ShowsCardsInTheOrderGiven()
    {
        // Arrange
        var tasks = new[] { Tasks.Make(3, "Third"), Tasks.Make(1, "First"), Tasks.Make(2, "Second") };

        // Act
        var cut = RenderLane(tasks);

        // Assert
        var titles = cut.FindAll(".card .t").Select(t => t.TextContent);
        Assert.Equal(["Third", "First", "Second"], titles);
    }

    [Fact]
    public void Lane_WithTasks_ShowsTheCountAndIsLabelledByTitle()
    {
        // Arrange
        var tasks = new[] { Tasks.Make(1), Tasks.Make(2) };

        // Act
        var cut = RenderLane(tasks);

        // Assert
        Assert.Equal("2", cut.Find(".lane-head .count").TextContent);
        Assert.Equal("Waiting", cut.Find("section").GetAttribute("aria-label"));
        Assert.Equal("Waiting", cut.Find("h2").TextContent);
    }

    [Fact]
    public void Lane_Empty_SaysNothingHere()
    {
        // Arrange
        IReadOnlyList<TaskVm> none = [];

        // Act
        var cut = RenderLane(none);

        // Assert
        Assert.Equal("Nothing here", cut.Find(".lane-empty").TextContent);
        Assert.Equal("0", cut.Find(".count").TextContent);
    }

    [Fact]
    public void Lane_WithoutQuickAdd_HasNoAddBox()
    {
        // Arrange
        var tasks = new[] { Tasks.Make(1) };

        // Act
        var cut = RenderLane(tasks, p => p.Add(l => l.QuickAdd, false));

        // Assert
        Assert.Empty(cut.FindAll("#quick-add"));
    }

    [Fact]
    public void Lane_WithQuickAdd_HasAnAddBoxAndNoEmptyMessage()
    {
        // Arrange
        IReadOnlyList<TaskVm> none = [];

        // Act
        var cut = RenderLane(none, p => p.Add(l => l.QuickAdd, true));

        // Assert
        Assert.NotNull(cut.Find("#quick-add"));
        Assert.Empty(cut.FindAll(".lane-empty"));
    }

    [Fact]
    public void Lane_EnterInQuickAdd_RaisesTheTitleAndClearsTheBox()
    {
        // Arrange
        var added = new List<string>();
        var cut = RenderLane([], p => { p.Add(l => l.QuickAdd, true); p.Add(l => l.OnQuickAdd, Record(added)); });
        var box = cut.Find("#quick-add");
        box.Input("Buy milk");

        // Act
        box.KeyDown(new KeyboardEventArgs { Key = "Enter" });

        // Assert
        Assert.Equal(["Buy milk"], added);
        Assert.Equal("", cut.Find("#quick-add").GetAttribute("value") ?? "");
    }

    [Fact]
    public void Lane_EnterInAnEmptyQuickAdd_AddsNothing()
    {
        // Arrange
        var added = new List<string>();
        var cut = RenderLane([], p => { p.Add(l => l.QuickAdd, true); p.Add(l => l.OnQuickAdd, Record(added)); });
        var box = cut.Find("#quick-add");
        box.Input("   ");

        // Act
        box.KeyDown(new KeyboardEventArgs { Key = "Enter" });

        // Assert
        Assert.Empty(added);
    }

    [Fact]
    public void Lane_SetDraft_PutsAFailedTitleBackInTheBox()
    {
        // Arrange
        var cut = RenderLane([], p => p.Add(l => l.QuickAdd, true));

        // Act
        cut.InvokeAsync(() => cut.Instance.SetDraft("Lost title"));

        // Assert
        Assert.Equal("Lost title", cut.Find("#quick-add").GetAttribute("value"));
    }

    [Fact]
    public void Lane_SetDraft_DoesNotOverwriteWhatWasTypedSince()
    {
        // Arrange
        var cut = RenderLane([], p => p.Add(l => l.QuickAdd, true));
        cut.Find("#quick-add").Input("Typed since");

        // Act
        cut.InvokeAsync(() => cut.Instance.SetDraft("Lost title"));

        // Assert
        Assert.Equal("Typed since", cut.Find("#quick-add").GetAttribute("value"));
    }

    [Fact]
    public void Lane_WhenPositionsAreLocked_IsMarkedNoDrop()
    {
        // Arrange
        var filter = new BoardFilter { Sort = SortMode.Title };

        // Assume
        Assert.False(filter.CanReorder);

        // Act
        var cut = RenderLane([Tasks.Make(1)], p => p.Add(l => l.NoDrop, !filter.CanReorder));

        // Assert
        Assert.Contains("no-drop", cut.Find("section").ClassList);
    }

    [Fact]
    public void Lane_WhenPositionsAreEditable_IsNotMarkedNoDrop()
    {
        // Arrange
        var filter = new BoardFilter { Sort = SortMode.LaneOrder };

        // Act
        var cut = RenderLane([Tasks.Make(1)], p => p.Add(l => l.NoDrop, !filter.CanReorder));

        // Assert
        Assert.DoesNotContain("no-drop", cut.Find("section").ClassList);
    }

    [Fact]
    public void Lane_DraggedOverACard_ReportsTheLaneAndTheCardAsTarget()
    {
        // Arrange
        var targets = new List<DropTarget>();
        var cut = RenderLane([Tasks.Make(5), Tasks.Make(6)], p => p.Add(l => l.OnDragOver, Record(targets)));

        // Act
        cut.FindAll(".card")[1].TriggerEvent("ondragover", new DragEventArgs());

        // Assert
        Assert.Equal([new DropTarget(BoardLane.Waiting, 6)], targets);
    }

    [Fact]
    public void Lane_DropIndicatorOverACard_LabelsTheDropSpot()
    {
        // Arrange
        var tasks = new[] { Tasks.Make(5), Tasks.Make(6) };

        // Act
        var cut = RenderLane(tasks, p => { p.Add(l => l.IsOver, true); p.Add(l => l.DragId, 99); p.Add(l => l.DropBeforeId, 6); });

        // Assert
        Assert.Equal("Drop: here", cut.Find(".drop-line .label").TextContent);
    }

    [Fact]
    public void Lane_WithArchivedTasks_ShowsTheHiddenCountAndLinksToTheArchive()
    {
        // Arrange
        const string href = "board/7/archive";

        // Act
        var cut = RenderLane([], p => { p.Add(l => l.HiddenCount, 3); p.Add(l => l.ArchiveHref, href); });

        // Assert
        Assert.Contains("3 older tasks hidden", cut.Find(".lane-hidden").TextContent);
        Assert.Equal(href, cut.Find(".lane-hidden a").GetAttribute("href"));
    }

    [Fact]
    public void Lane_OneArchivedTask_SaysTaskInTheSingular()
    {
        // Arrange
        const int hidden = 1;

        // Act
        var cut = RenderLane([], p => { p.Add(l => l.HiddenCount, hidden); p.Add(l => l.ArchiveHref, "board/1/archive"); });

        // Assert
        Assert.Contains("1 older task hidden", cut.Find(".lane-hidden").TextContent);
    }

    [Fact]
    public void Lane_NothingArchived_ShowsNoFooter()
    {
        // Arrange
        const int hidden = 0;

        // Act
        var cut = RenderLane([], p => { p.Add(l => l.HiddenCount, hidden); p.Add(l => l.ArchiveHref, "board/1/archive"); });

        // Assert
        Assert.Empty(cut.FindAll(".lane-hidden"));
    }

    [Fact]
    public void Lane_CardClicked_PassesTheTaskIdOn()
    {
        // Arrange
        var opened = new List<int>();
        var cut = RenderLane([Tasks.Make(8), Tasks.Make(9)], p => p.Add(l => l.OnOpen, Record(opened)));

        // Act
        cut.FindAll(".card")[1].Click();

        // Assert
        Assert.Equal([9], opened);
    }
}
