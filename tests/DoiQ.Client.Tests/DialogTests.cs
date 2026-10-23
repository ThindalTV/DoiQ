using Bunit;
using DoiQ.Client.Components;

namespace DoiQ.Client.Tests;

public sealed class DialogTests : ClientTestBase
{
    private IRenderedComponent<ConfirmDialog> RenderConfirm(Action<ComponentParameterCollectionBuilder<ConfirmDialog>>? more = null) =>
        Render<ConfirmDialog>(p =>
        {
            p.Add(d => d.Title, "Delete this board?");
            p.AddChildContent("<p>This cannot be undone.</p>");
            more?.Invoke(p);
        });

    [Fact]
    public void ConfirmDialog_Opened_IsAModalAlertDialogWithTitleAndBody()
    {
        // Arrange
        // Act
        var cut = RenderConfirm();

        // Assert
        var dialog = cut.Find("#confirm-dialog");
        Assert.Equal("alertdialog", dialog.GetAttribute("role"));
        Assert.Equal("true", dialog.GetAttribute("aria-modal"));
        Assert.Equal("Delete this board?", cut.Find("#confirm-title").TextContent);
        Assert.Contains("This cannot be undone.", cut.Find("#confirm-body").TextContent);
        Assert.Equal("confirm-title", dialog.GetAttribute("aria-labelledby"));
    }

    [Fact]
    public void ConfirmDialog_Opened_PutsFocusOnCancelAndTrapsTab()
    {
        // Arrange
        // Act
        var cut = RenderConfirm();

        // Assert
        var target = Assert.IsType<Microsoft.AspNetCore.Components.ElementReference>(JSInterop.VerifyFocusAsyncInvoke().Arguments[0]);
        Assert.Contains(target.Id, cut.Find("#confirm-cancel").OuterHtml);
        JSInterop.VerifyInvoke("doiq.trapFocus");
    }

    [Fact]
    public void ConfirmDialog_Cancel_RaisesOnCancelOnly()
    {
        // Arrange
        var cancelled = 0;
        var confirmed = 0;
        var cut = RenderConfirm(p => { p.Add(d => d.OnCancel, Count(() => cancelled++)); p.Add(d => d.OnConfirm, Count(() => confirmed++)); });

        // Act
        cut.Find("#confirm-cancel").Click();

        // Assert
        Assert.Equal((1, 0), (cancelled, confirmed));
    }

    [Fact]
    public void ConfirmDialog_ClickingTheScrim_Cancels()
    {
        // Arrange
        var cancelled = 0;
        var cut = RenderConfirm(p => p.Add(d => d.OnCancel, Count(() => cancelled++)));

        // Act
        cut.Find(".scrim").Click();

        // Assert
        Assert.Equal(1, cancelled);
    }

    [Fact]
    public void ConfirmDialog_Confirm_RaisesOnConfirmOnly()
    {
        // Arrange
        var cancelled = 0;
        var confirmed = 0;
        var cut = RenderConfirm(p => { p.Add(d => d.OnCancel, Count(() => cancelled++)); p.Add(d => d.OnConfirm, Count(() => confirmed++)); });

        // Act
        cut.Find("#confirm-ok").Click();

        // Assert
        Assert.Equal((0, 1), (cancelled, confirmed));
    }

    [Fact]
    public void ConfirmDialog_Busy_DisablesBothButtons()
    {
        // Arrange
        // Act
        var cut = RenderConfirm(p => p.Add(d => d.Busy, true));

        // Assert
        Assert.True(cut.Find("#confirm-cancel").HasAttribute("disabled"));
        Assert.True(cut.Find("#confirm-ok").HasAttribute("disabled"));
    }

    [Fact]
    public void ConfirmDialog_CustomLabels_AreShownOnTheButtons()
    {
        // Arrange
        // Act
        var cut = RenderConfirm(p => { p.Add(d => d.ConfirmText, "Delete Home"); p.Add(d => d.CancelText, "Keep it"); });

        // Assert
        Assert.Equal("Delete Home", cut.Find("#confirm-ok").TextContent);
        Assert.Equal("Keep it", cut.Find("#confirm-cancel").TextContent);
    }

    [Fact]
    public void ShortcutsDialog_Opened_IsATitledDialogListingEveryGroup()
    {
        // Arrange
        // Act
        var cut = Render<ShortcutsDialog>();

        // Assert
        Assert.Equal("dialog", cut.Find(".dialog").GetAttribute("role"));
        Assert.Equal("Keyboard shortcuts", cut.Find("#sc-title").TextContent);
        var groups = cut.FindAll(".sc-grid h2").Select(h => h.TextContent);
        Assert.Equal(["Move around", "Board", "Boards", "Focused task", "In the drawer"], groups);
    }

    [Fact]
    public void ShortcutsDialog_Opened_ExplainsTheKeysWithTheirMeaning()
    {
        // Arrange
        // Act
        var cut = Render<ShortcutsDialog>();

        // Assert
        var keys = cut.FindAll("dt").Select(d => d.TextContent).ToList();
        var meanings = cut.FindAll("dd").Select(d => d.TextContent).ToList();
        Assert.Equal(keys.Count, meanings.Count);
        Assert.Contains("J / K", keys);
        Assert.Contains("Take it", meanings);
    }

    [Fact]
    public void ShortcutsDialog_CloseButton_RaisesOnClose()
    {
        // Arrange
        var closed = 0;
        var cut = Render<ShortcutsDialog>(p => p.Add(d => d.OnClose, Count(() => closed++)));

        // Act
        cut.Find("button.close").Click();

        // Assert
        Assert.Equal(1, closed);
        Assert.Equal("Close (Esc)", cut.Find("button.close").GetAttribute("aria-label"));
    }

    [Fact]
    public void ShortcutsDialog_ClickingTheScrim_Closes()
    {
        // Arrange
        var closed = 0;
        var cut = Render<ShortcutsDialog>(p => p.Add(d => d.OnClose, Count(() => closed++)));

        // Act
        cut.Find(".scrim").Click();

        // Assert
        Assert.Equal(1, closed);
    }
}
