using Bunit;
using DoiQ.Client.Components;
using DoiQ.Client.Models;
using DoiQ.Client.Services;

namespace DoiQ.Client.Tests;

public sealed class TopBarTests : ClientTestBase
{
    private IRenderedComponent<TopBar> RenderBar(BoardFilter? filter = null, Action<ComponentParameterCollectionBuilder<TopBar>>? more = null) =>
        Render<TopBar>(p =>
        {
            p.Add(b => b.Filter, filter ?? new BoardFilter());
            more?.Invoke(p);
        });

    [Fact]
    public void TopBar_TypingInSearch_UpdatesTheFilterAndRaisesChanged()
    {
        // Arrange
        var filter = new BoardFilter();
        var changed = 0;
        var cut = RenderBar(filter, p => p.Add(b => b.OnChanged, Count(() => changed++)));

        // Act
        cut.Find("#search").Input("venue");

        // Assert
        Assert.Equal("venue", filter.Search);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void TopBar_MeButton_FiltersToTheUser()
    {
        // Arrange
        var filter = new BoardFilter();
        var cut = RenderBar(filter);

        // Act
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Me").Click();

        // Assert
        Assert.Equal(AssigneeFilter.Me, filter.Assignee);
        Assert.True(IsOn(cut.FindAll("button").Single(b => b.TextContent.Trim() == "Me"), "aria-pressed"));
    }

    [Fact]
    public void TopBar_UnassignedButton_FiltersToUnassigned()
    {
        // Arrange
        var filter = new BoardFilter { Assignee = AssigneeFilter.Me };
        var cut = RenderBar(filter);

        // Act
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Unassigned").Click();

        // Assert
        Assert.Equal(AssigneeFilter.Unassigned, filter.Assignee);
    }

    [Fact]
    public void TopBar_PickingAnAgent_FiltersToThatAgentAndNamesTheButton()
    {
        // Arrange
        var filter = new BoardFilter();
        var cut = RenderBar(filter, p => p.Add(b => b.Agents, ["Hermes", "Hermes 2"]));
        cut.Find(".seg [aria-haspopup=menu]").Click();

        // Act
        cut.FindAll("[role=menuitemradio]").Single(i => i.TextContent == "Hermes 2").Click();

        // Assert
        Assert.Equal(AssigneeFilter.Agents, filter.Assignee);
        Assert.Equal("Hermes 2", filter.AgentName);
        Assert.Contains("Hermes 2", cut.Find(".seg [aria-haspopup=menu]").TextContent);
        Assert.Empty(cut.FindAll(".menu"));
    }

    [Fact]
    public void TopBar_AnyAgent_ClearsTheAgentName()
    {
        // Arrange
        var filter = new BoardFilter { Assignee = AssigneeFilter.Agents, AgentName = "Hermes" };
        var cut = RenderBar(filter, p => p.Add(b => b.Agents, ["Hermes"]));
        cut.Find(".seg [aria-haspopup=menu]").Click();

        // Act
        cut.FindAll("[role=menuitemradio]").Single(i => i.TextContent == "Any agent").Click();

        // Assert
        Assert.Null(filter.AgentName);
        Assert.Equal(AssigneeFilter.Agents, filter.Assignee);
    }

    [Fact]
    public void TopBar_NeedsMe_ShowsTheCountAndTogglesTheFilter()
    {
        // Arrange
        var filter = new BoardFilter();
        var cut = RenderBar(filter, p => p.Add(b => b.NeedsMeCount, 4));
        var button = cut.FindAll("button").Single(b => b.TextContent.Contains("Needs me"));

        // Act
        button.Click();

        // Assert
        Assert.Equal("4", cut.Find(".count-badge").TextContent);
        Assert.True(filter.NeedsMe);
        Assert.True(IsOn(cut.FindAll("button").Single(b => b.TextContent.Contains("Needs me")), "aria-pressed"));
    }

    [Fact]
    public void TopBar_ChoosingASort_UpdatesTheFilter()
    {
        // Arrange
        var filter = new BoardFilter();
        var cut = RenderBar(filter);

        // Act
        cut.Find("select").Change(nameof(SortMode.DueDate));

        // Assert
        Assert.Equal(SortMode.DueDate, filter.Sort);
    }

    [Fact]
    public void TopBar_SortedByPriority_ShowsNoReorderHint()
    {
        // Arrange
        var filter = new BoardFilter { Sort = SortMode.LaneOrder };

        // Act
        var cut = RenderBar(filter);

        // Assert
        Assert.Empty(cut.FindAll(".reorder-hint"));
    }

    [Theory]
    [InlineData(SortMode.RecentlyUpdated, "recently updated")]
    [InlineData(SortMode.Created, "created")]
    [InlineData(SortMode.DueDate, "due date")]
    [InlineData(SortMode.Title, "title")]
    public void TopBar_SortedByAnythingElse_ExplainsThatReorderingIsOff(SortMode sort, string name)
    {
        // Arrange
        var filter = new BoardFilter { Sort = sort };

        // Assume
        Assert.False(filter.CanReorder);

        // Act
        var cut = RenderBar(filter);

        // Assert
        Assert.Equal($"Reordering is off while sorted by {name}", cut.Find(".reorder-hint").TextContent);
    }

    [Fact]
    public void TopBar_WithABoard_LinksToItsArchive()
    {
        // Arrange
        const int boardId = 4;

        // Act
        var cut = RenderBar(more: p => p.Add(b => b.CurrentBoardId, boardId));

        // Assert
        Assert.Equal("board/4/archive", cut.FindAll("a.btn").Single(a => a.TextContent == "Archive").GetAttribute("href"));
    }

    [Theory]
    [InlineData(HubState.Live, "Live", "conn-live")]
    [InlineData(HubState.Reconnecting, "Reconnecting", "conn-reconnecting")]
    [InlineData(HubState.Offline, "Offline", "conn-offline")]
    [InlineData(HubState.Connecting, "Connecting", "conn-connecting")]
    public void TopBar_ConnectionState_ShowsLabelAndClassAsAStatus(HubState state, string label, string cssClass)
    {
        // Arrange
        // Act
        var cut = RenderBar(null, p => p.Add(b => b.Connection, state));

        // Assert
        var conn = cut.Find(".conn");
        Assert.Equal(label, conn.TextContent.Trim());
        Assert.Contains(cssClass, conn.ClassList);
        Assert.Equal("status", conn.GetAttribute("role"));
        Assert.False(string.IsNullOrWhiteSpace(conn.GetAttribute("title")));
    }

    [Fact]
    public void TopBar_Offline_TellsTheUserTheBoardCatchesUpLater()
    {
        // Arrange
        // Act
        var cut = RenderBar(null, p => p.Add(b => b.Connection, HubState.Offline));

        // Assert
        Assert.Contains("No connection", cut.Find(".conn").GetAttribute("title"));
    }

    [Fact]
    public void TopBar_Presence_ShowsTheGivenText()
    {
        // Arrange
        // Act
        var cut = RenderBar(null, p => p.Add(b => b.PresenceText, "2 agents active"));

        // Assert
        Assert.Contains("2 agents active", cut.Find(".presence").TextContent);
    }

    [Fact]
    public void TopBar_BoardSwitcher_ShowsTheCurrentBoardNameWithTheMenuClosed()
    {
        // Arrange
        var boards = new[] { Tasks.Board(1, "Main"), Tasks.Board(2, "Home") };

        // Act
        var cut = RenderBar(null, p => { p.Add(b => b.Boards, boards); p.Add(b => b.CurrentBoardId, 2); });

        // Assert
        Assert.Equal("Home", cut.Find("#board-switch .board-name").TextContent);
        Assert.False(IsOn(cut.Find("#board-switch"), "aria-expanded"));
        Assert.Empty(cut.FindAll(".board-menu"));
    }

    [Fact]
    public void TopBar_BoardSwitcherOpened_ListsBoardsWithOpenCountsAndMarksTheCurrentOne()
    {
        // Arrange
        var boards = new[] { Tasks.Board(1, "Main", open: 5), Tasks.Board(2, "Home", open: 1) };
        var cut = RenderBar(null, p => { p.Add(b => b.Boards, boards); p.Add(b => b.CurrentBoardId, 1); });

        // Act
        cut.Find("#board-switch").Click();

        // Assert
        var items = cut.FindAll(".board-menu [role=menuitemradio]");
        Assert.Equal(2, items.Count);
        Assert.True(IsOn(items[0], "aria-checked"));
        Assert.False(IsOn(items[1], "aria-checked"));
        Assert.Contains("5 open", items[0].TextContent);
        Assert.Contains(cut.FindAll(".board-menu a"), a => a.TextContent.Contains("Manage boards"));
    }

    [Fact]
    public void TopBar_ToggleButtons_StateIsExposedAsTrueOrFalseStrings()
    {
        // Arrange
        var cut = RenderBar(new BoardFilter { Assignee = AssigneeFilter.Me });

        // Act
        var pressed = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Me").GetAttribute("aria-pressed");
        var notPressed = cut.FindAll("button").Single(b => b.TextContent.Trim() == "All").GetAttribute("aria-pressed");

        // Assert
        Assert.Equal(("true", "false"), (pressed, notPressed));
    }

    [Fact]
    public void TopBar_PickingABoard_RaisesItsIdAndClosesTheMenu()
    {
        // Arrange
        var switched = new List<int>();
        var boards = new[] { Tasks.Board(1, "Main"), Tasks.Board(2, "Home") };
        var cut = RenderBar(null, p => { p.Add(b => b.Boards, boards); p.Add(b => b.CurrentBoardId, 1); p.Add(b => b.OnSwitchBoard, Record(switched)); });
        cut.Find("#board-switch").Click();

        // Act
        cut.FindAll(".board-menu [role=menuitemradio]")[1].Click();

        // Assert
        Assert.Equal([2], switched);
        Assert.Empty(cut.FindAll(".board-menu"));
    }

    [Fact]
    public void TopBar_NewTaskButton_RaisesOnNewTask()
    {
        // Arrange
        var raised = 0;
        var cut = RenderBar(null, p => p.Add(b => b.OnNewTask, Count(() => raised++)));

        // Act
        cut.FindAll("button").Single(b => b.TextContent.Contains("New task")).Click();

        // Assert
        Assert.Equal(1, raised);
    }

    [Fact]
    public void TopBar_ShortcutsAndSignOut_RaiseTheirCallbacks()
    {
        // Arrange
        var shortcuts = 0;
        var signOut = 0;
        var cut = RenderBar(null, p => { p.Add(b => b.OnShowShortcuts, Count(() => shortcuts++)); p.Add(b => b.OnSignOut, Count(() => signOut++)); });

        // Act
        cut.Find("[aria-label='Keyboard shortcuts']").Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Sign out").Click();

        // Assert
        Assert.Equal((1, 1), (shortcuts, signOut));
    }
}
