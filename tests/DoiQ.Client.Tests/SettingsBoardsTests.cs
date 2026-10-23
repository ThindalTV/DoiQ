using AngleSharp.Dom;
using Bunit;
using DoiQ.Client.Components;
using DoiQ.Client.Models;
using DoiQ.Client.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace DoiQ.Client.Tests;

public sealed class SettingsBoardsTests : ClientTestBase
{
    /// <summary>Boards: Main (the seeded sample tasks, some open), "Busy" (one open task) and "Empty" (nothing on it).</summary>
    private async Task<IRenderedComponent<Settings>> RenderSettings()
    {
        var busy = await Api.CreateBoardAsync("Busy");
        await Api.SelectBoardAsync(busy.Id);
        await Api.CreateTaskAsync("Still open");
        await Api.CreateBoardAsync("Empty");
        await Api.SelectBoardAsync(1);
        return Render<Settings>();
    }

    private static IElement Row(IRenderedComponent<Settings> cut, string board) =>
        cut.FindAll(".board-row").Single(r => r.QuerySelector("h4")?.TextContent == board);

    private static IElement DeleteButton(IRenderedComponent<Settings> cut, string board) => Row(cut, board).QuerySelector("button.btn-danger")!;

    [Fact]
    public async Task Settings_Boards_ListEachWithItsOpenAndDoneCounts()
    {
        // Arrange
        // Act
        var cut = await RenderSettings();

        // Assert
        Assert.Equal(["Main", "Busy", "Empty"], cut.FindAll(".board-row h4").Select(h => h.TextContent));
        Assert.Equal("1 open · 0 done", Row(cut, "Busy").QuerySelector(".board-counts")!.TextContent);
        Assert.Equal("0 open · 0 done", Row(cut, "Empty").QuerySelector(".board-counts")!.TextContent);
    }

    [Fact]
    public async Task Settings_BoardWithOpenTasks_HasDeleteDisabledWithTheReasonLinked()
    {
        // Arrange
        var cut = await RenderSettings();

        // Act
        var button = DeleteButton(cut, "Busy");

        // Assert
        Assert.Equal("true", button.GetAttribute("aria-disabled"));
        var reason = cut.Find("#" + button.GetAttribute("aria-describedby"));
        Assert.Equal("1 task is not done. Finish or delete it first.", reason.TextContent);
    }

    [Fact]
    public async Task Settings_BoardWithSeveralOpenTasks_PluralisesTheReason()
    {
        // Arrange
        var cut = await RenderSettings();
        var open = Api.Boards.Single(b => b.Id == 1).OpenTasks;

        // Act
        var reason = cut.Find("#" + DeleteButton(cut, "Main").GetAttribute("aria-describedby"));

        // Assert
        Assert.Equal($"{open} tasks are not done. Finish or delete them first.", reason.TextContent);
    }

    [Fact]
    public async Task Settings_DeleteOnABoardWithOpenTasks_OpensNoDialog()
    {
        // Arrange
        var cut = await RenderSettings();

        // Act
        DeleteButton(cut, "Busy").Click();

        // Assert
        Assert.Empty(cut.FindAll("#confirm-dialog"));
        Assert.Contains(await Api.GetBoardsAsync(), b => b.Name == "Busy");
    }

    [Fact]
    public async Task Settings_EmptyBoard_HasDeleteEnabledAndNoReason()
    {
        // Arrange
        var cut = await RenderSettings();

        // Act
        var button = DeleteButton(cut, "Empty");

        // Assert
        Assert.False(button.HasAttribute("aria-disabled"));
        Assert.False(button.HasAttribute("aria-describedby"));
        Assert.Empty(Row(cut, "Empty").QuerySelectorAll(".board-why"));
    }

    [Fact]
    public void Settings_TheOnlyBoard_CannotBeDeleted()
    {
        // Arrange
        // Act
        var cut = Render<Settings>();

        // Assert
        var button = DeleteButton(cut, "Main");
        Assert.Equal("true", button.GetAttribute("aria-disabled"));
        Assert.Contains("last board", cut.Find("#" + button.GetAttribute("aria-describedby")).TextContent);
    }

    [Fact]
    public async Task Settings_DeleteOnAnEmptyBoard_AsksForConfirmationNamingTheBoard()
    {
        // Arrange
        var cut = await RenderSettings();

        // Act
        DeleteButton(cut, "Empty").Click();

        // Assert
        Assert.Equal("Delete this board?", cut.Find("#confirm-title").TextContent);
        Assert.Equal("Delete Empty", cut.Find("#confirm-ok").TextContent);
        Assert.Contains("Empty", cut.Find("#confirm-body").TextContent);
        Assert.Contains(await Api.GetBoardsAsync(), b => b.Name == "Empty");
    }

    [Fact]
    public async Task Settings_ConfirmingTheDelete_RemovesTheBoardAndSaysSo()
    {
        // Arrange
        var cut = await RenderSettings();
        DeleteButton(cut, "Empty").Click();

        // Act
        cut.Find("#confirm-ok").Click();

        // Assert
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#confirm-dialog")));
        Assert.DoesNotContain(await Api.GetBoardsAsync(), b => b.Name == "Empty");
        Assert.DoesNotContain("Empty", cut.FindAll(".board-row h4").Select(h => h.TextContent));
        Assert.Contains(Notices.Items, n => n.Text.Contains("deleted"));
    }

    [Fact]
    public async Task Settings_CancellingTheDelete_KeepsTheBoardAndClosesTheDialog()
    {
        // Arrange
        var cut = await RenderSettings();
        DeleteButton(cut, "Empty").Click();

        // Act
        cut.Find("#confirm-cancel").Click();

        // Assert
        Assert.Empty(cut.FindAll("#confirm-dialog"));
        Assert.Contains(await Api.GetBoardsAsync(), b => b.Name == "Empty");
    }

    [Fact]
    public async Task Settings_EscapeWhileTheDialogIsOpen_ClosesTheDialogAndStaysOnThePage()
    {
        // Arrange
        var cut = await RenderSettings();
        DeleteButton(cut, "Empty").Click();
        var urlBefore = Services.GetRequiredService<NavigationManager>().Uri;

        // Act
        await cut.InvokeAsync(() => cut.Instance.OnKey("Escape", false, false));

        // Assert
        Assert.Empty(cut.FindAll("#confirm-dialog"));
        Assert.Equal(urlBefore, Services.GetRequiredService<NavigationManager>().Uri);
        Assert.Contains(await Api.GetBoardsAsync(), b => b.Name == "Empty");
    }

    [Fact]
    public async Task Settings_AddBoard_CreatesItAndClearsTheBox()
    {
        // Arrange
        var cut = await RenderSettings();
        cut.Find("#new-board").Input("Garden");

        // Act
        cut.Find("form.board-add").Submit();

        // Assert
        cut.WaitForAssertion(() => Assert.Contains("Garden", cut.FindAll(".board-row h4").Select(h => h.TextContent)));
        Assert.Equal("", cut.Find("#new-board").GetAttribute("value") ?? "");
    }

    [Fact]
    public async Task Settings_AddBoardWithoutAName_IsDisabled()
    {
        // Arrange
        var cut = await RenderSettings();

        // Act
        cut.Find("#new-board").Input("   ");

        // Assert
        Assert.True(cut.Find("form.board-add button[type=submit]").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Settings_AddingADuplicateBoardName_ShowsTheServersRefusalAsANotice()
    {
        // Arrange
        var cut = await RenderSettings();
        cut.Find("#new-board").Input("main");

        // Act
        cut.Find("form.board-add").Submit();

        // Assert
        cut.WaitForAssertion(() => Assert.Contains(Notices.Items, n => n.Text.Contains("already exists")));
        Assert.Equal(3, cut.FindAll(".board-row").Count);
    }

    [Fact]
    public async Task Settings_RenameBoard_SavesTheNewName()
    {
        // Arrange
        var cut = await RenderSettings();
        Row(cut, "Empty").QuerySelectorAll("button").Single(b => b.TextContent == "Rename").Click();
        cut.Find("input[id^=rename-]").Input("Spare");

        // Act
        cut.Find("form.inline").Submit();

        // Assert
        cut.WaitForAssertion(() => Assert.Contains("Spare", cut.FindAll(".board-row h4").Select(h => h.TextContent)));
        Assert.DoesNotContain(await Api.GetBoardsAsync(), b => b.Name == "Empty");
    }
}
