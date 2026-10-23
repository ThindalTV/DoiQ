using Bunit;
using DoiQ.Client.Models;
using DoiQ.Client.Pages;
using DoiQ.Client.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace DoiQ.Client.Tests;

/// <summary>
/// The archive view (/board/{id}/archive) and the board's archive entry points, against the in-memory client. Its Main
/// board holds six archived tasks: 14 to 17 (done 34 to 37 days ago) and 18 and 19 (98 and 99 days ago).
/// </summary>
public sealed class ArchiveViewTests : ClientTestBase
{
    public ArchiveViewTests() => Services.AddSingleton<BoardHubClient>();

    private IRenderedComponent<Archive> RenderArchive(int boardId = 1) => Render<Archive>(p => p.Add(a => a.BoardId, boardId));

    private static List<string> Titles(IRenderedComponent<Archive> cut) => cut.FindAll(".archive-item .t").Select(e => e.TextContent).ToList();

    [Fact]
    public void Archive_Opened_ListsTheArchivedTasksMostRecentlyDoneFirst()
    {
        // Arrange
        const int main = 1;

        // Act
        var cut = RenderArchive(main);

        // Assert
        cut.WaitForAssertion(() => Assert.Equal(
            ["Pick a name for the project", "Sketch the lane layout", "Choose SQLite over Postgres", "Write the build plan", "Try the old kanban app", "Evaluate hosted boards"],
            Titles(cut)));
        Assert.Empty(cut.FindAll(".archive-more"));
    }

    [Fact]
    public void Archive_Line_ShowsTheDoneDateAndTheAssignee()
    {
        // Arrange
        // Act
        var cut = RenderArchive();

        // Assert
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".archive-item")));
        var line = cut.FindAll(".archive-item")[0];
        Assert.StartsWith("Done ", line.QuerySelector(".done")!.TextContent);
        Assert.Contains(Fmt.HumanDisplay, line.QuerySelector(".who")!.TextContent);
    }

    [Fact]
    public void Archive_MoreThanAPage_LoadMoreAppendsTheNextPage()
    {
        // Arrange
        Api.ArchivePageSize = 4;
        var cut = RenderArchive();
        cut.WaitForAssertion(() => Assert.Equal(4, Titles(cut).Count));

        // Act
        cut.Find(".archive-more").Click();

        // Assert
        cut.WaitForAssertion(() => Assert.Equal(6, Titles(cut).Count));
        Assert.Equal(6, Titles(cut).Distinct().Count());
        Assert.Empty(cut.FindAll(".archive-more"));
    }

    [Fact]
    public void Archive_TypingASearch_ListsOnlyTheMatches()
    {
        // Arrange
        var cut = RenderArchive();
        cut.WaitForAssertion(() => Assert.Equal(6, Titles(cut).Count));

        // Act
        cut.Find("#archive-search").Input("sqlite");

        // Assert
        cut.WaitForAssertion(() => Assert.Equal(["Choose SQLite over Postgres"], Titles(cut)), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Archive_SearchWithoutMatches_SaysSo()
    {
        // Arrange
        var cut = RenderArchive();
        cut.WaitForAssertion(() => Assert.Equal(6, Titles(cut).Count));

        // Act
        cut.Find("#archive-search").Input("nothing like this");

        // Assert
        cut.WaitForAssertion(() => Assert.Equal("No archived task matches “nothing like this”.", cut.Find(".archive-empty").TextContent), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Archive_QueryInTheAddress_OpensAlreadySearched()
    {
        // Arrange
        Services.GetRequiredService<NavigationManager>().NavigateTo("board/1/archive?q=kanban");

        // Act
        var cut = RenderArchive();

        // Assert
        cut.WaitForAssertion(() => Assert.Equal(["Try the old kanban app"], Titles(cut)));
        Assert.Equal("kanban", cut.Find("#archive-search").GetAttribute("value"));
    }

    [Fact]
    public async Task Archive_BoardWithNothingArchived_SaysSo()
    {
        // Arrange
        var empty = await Api.CreateBoardAsync("Empty");

        // Act
        var cut = RenderArchive(empty.Id);

        // Assert
        cut.WaitForAssertion(() => Assert.Equal("Nothing is archived on this board.", cut.Find(".archive-empty").TextContent));
    }

    [Fact]
    public void Archive_LineClicked_OpensTheTaskInTheDrawer()
    {
        // Arrange
        var cut = RenderArchive();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".archive-item")));

        // Act
        cut.FindAll(".archive-item")[0].Click();

        // Assert
        cut.WaitForAssertion(() => Assert.Equal("Pick a name for the project", cut.Find("#task-title").GetAttribute("value")));
    }

    [Fact]
    public async Task Archive_TaskReopenedFromTheDrawer_LeavesTheListAndIsBackOnTheBoard()
    {
        // Arrange
        var cut = RenderArchive();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".archive-item")));
        cut.FindAll(".archive-item")[0].Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#task-status")));

        // Act
        cut.Find("#task-status").Change(nameof(BoardLane.New));

        // Assert
        cut.WaitForAssertion(() => Assert.DoesNotContain("Pick a name for the project", Titles(cut)));
        Assert.Contains(await Api.GetBoard(Ct), t => t.Title == "Pick a name for the project" && t.Lane == BoardLane.New);
        Assert.Contains(Notices.Items, n => n.Text.Contains("is back on the board"));
    }
}

/// <summary>The board's way into the archive: the Done lane's hidden count and the Archived group of the search.</summary>
public sealed class BoardArchiveTests : ClientTestBase
{
    public BoardArchiveTests() => Services.AddSingleton<BoardHubClient>();

    private IRenderedComponent<Board> RenderBoard()
    {
        var cut = Render<Board>(p => p.Add(b => b.BoardId, 1));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".card")));
        return cut;
    }

    private static List<string> ArchivedGroup(IRenderedComponent<Board> cut) =>
        cut.FindAll(".archived-results .archive-item .t").Select(e => e.TextContent).ToList();

    [Fact]
    public void Board_DoneLane_ShowsTheArchivedCountWithALinkToTheArchive()
    {
        // Arrange
        // Act
        var cut = RenderBoard();

        // Assert
        var footer = cut.Find(".lane-hidden");
        Assert.Contains("6 older tasks hidden", footer.TextContent);
        Assert.Equal("board/1/archive", footer.QuerySelector("a")!.GetAttribute("href"));
    }

    [Fact]
    public void Board_Loaded_ShowsNoArchivedTaskAsACard()
    {
        // Arrange
        // Act
        var cut = RenderBoard();

        // Assert
        Assert.DoesNotContain(cut.FindAll(".card .t"), c => c.TextContent == "Try the old kanban app");
    }

    [Fact]
    public void Board_SearchMatchingAnArchivedTask_ShowsItInTheArchivedGroup()
    {
        // Arrange
        var cut = RenderBoard();

        // Act
        cut.Find("#search").Input("kanban");

        // Assert
        cut.WaitForAssertion(() => Assert.Equal(["Try the old kanban app"], ArchivedGroup(cut)), TimeSpan.FromSeconds(5));
        Assert.Empty(cut.FindAll(".archived-results .btn-link"));
    }

    [Fact]
    public void Board_SearchMatchingOnlyActiveTasks_ShowsNoArchivedGroup()
    {
        // Arrange
        var cut = RenderBoard();

        // Act
        cut.Find("#search").Input("backup");

        // Assert
        cut.WaitForAssertion(() => Assert.Equal(["Set up a nightly backup of todo.db"], cut.FindAll(".card .t").Select(c => c.TextContent)), TimeSpan.FromSeconds(5));
        Assert.Empty(cut.FindAll(".archived-results"));
    }

    [Fact]
    public void Board_MoreArchivedMatchesThanTheGroupHolds_LinksToTheArchiveSearch()
    {
        // Arrange
        for (var i = 0; i < 12; i++) Api.AddArchived(500 + i, $"garden chore {i}", DateTimeOffset.Now.AddDays(-30 - i));
        var cut = RenderBoard();

        // Act
        cut.Find("#search").Input("garden");

        // Assert
        cut.WaitForAssertion(() => Assert.Equal(10, ArchivedGroup(cut).Count), TimeSpan.FromSeconds(5));
        Assert.Equal("board/1/archive?q=garden", cut.Find(".archived-results .btn-link").GetAttribute("href"));
    }

    [Fact]
    public void Board_ArchivedSearchResultClicked_OpensItInTheDrawer()
    {
        // Arrange
        var cut = RenderBoard();
        cut.Find("#search").Input("kanban");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".archived-results .archive-item")), TimeSpan.FromSeconds(5));

        // Act
        cut.Find(".archived-results .archive-item").Click();

        // Assert
        cut.WaitForAssertion(() => Assert.Equal("Try the old kanban app", cut.Find("#task-title").GetAttribute("value")));
    }
}
