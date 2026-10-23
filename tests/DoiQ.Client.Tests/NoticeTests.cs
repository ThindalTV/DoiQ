using System.Net;
using Bunit;
using DoiQ.Client.Components;
using DoiQ.Client.Models;
using DoiQ.Client.Services;

namespace DoiQ.Client.Tests;

public sealed class NoticeTests : ClientTestBase
{
    [Fact]
    public void NoticeHost_Error_IsAnAlertAndHasNoTimerToDisappear()
    {
        // Arrange
        var cut = Render<NoticeHost>();

        // Act
        cut.InvokeAsync(() => Notices.Show(NoticeLevel.Error, "Saving failed"));

        // Assert
        var notice = cut.Find(".notice");
        Assert.Equal("alert", notice.GetAttribute("role"));
        Assert.Contains("notice-error", notice.ClassList);
        Assert.Equal("Saving failed", cut.Find(".notice-text").TextContent);
    }

    [Fact]
    public void NoticeHost_Info_IsAStatus()
    {
        // Arrange
        var cut = Render<NoticeHost>();

        // Act
        cut.InvokeAsync(() => Notices.Info("Board added"));

        // Assert
        Assert.Equal("status", cut.Find(".notice").GetAttribute("role"));
    }

    [Fact]
    public void NoticeHost_Warning_IsAnAlert()
    {
        // Arrange
        var cut = Render<NoticeHost>();

        // Act
        cut.InvokeAsync(() => Notices.Show(NoticeLevel.Warning, "Changed elsewhere"));

        // Assert
        Assert.Equal("alert", cut.Find(".notice").GetAttribute("role"));
        Assert.Contains("notice-warning", cut.Find(".notice").ClassList);
    }

    [Fact]
    public void NoticeHost_Info_DismissesItselfWhileAnErrorStays()
    {
        // Arrange
        var cut = Render<NoticeHost>();
        cut.InvokeAsync(() =>
        {
            Notices.Show(NoticeLevel.Error, "Sticky");
            Notices.Info("Fleeting");
        });

        // Assume
        Assert.Equal(2, cut.FindAll(".notice").Count);

        // Act
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".notice")), TimeSpan.FromSeconds(10));

        // Assert
        Assert.Equal("Sticky", cut.Find(".notice-text").TextContent);
    }

    [Fact]
    public void NoticeHost_DismissButton_RemovesTheNotice()
    {
        // Arrange
        var cut = Render<NoticeHost>();
        cut.InvokeAsync(() => Notices.Show(NoticeLevel.Error, "Gone soon"));

        // Act
        cut.Find("button[aria-label=Dismiss]").Click();

        // Assert
        Assert.Empty(cut.FindAll(".notice"));
        Assert.Empty(Notices.Items);
    }

    [Fact]
    public void NoticeHost_ActionButton_RunsTheActionAndDismisses()
    {
        // Arrange
        var ran = 0;
        var cut = Render<NoticeHost>();
        cut.InvokeAsync(() => Notices.Show(NoticeLevel.Error, "Could not save", "Retry", () => { ran++; return Task.CompletedTask; }));

        // Act
        cut.FindAll("button").Single(b => b.TextContent == "Retry").Click();

        // Assert
        Assert.Equal(1, ran);
        Assert.Empty(cut.FindAll(".notice"));
    }

    [Fact]
    public void NoticeHost_NoticeWithoutAction_HasOnlyTheDismissButton()
    {
        // Arrange
        var cut = Render<NoticeHost>();

        // Act
        cut.InvokeAsync(() => Notices.Show(NoticeLevel.Error, "Plain"));

        // Assert
        Assert.Single(cut.FindAll(".notice button"));
    }

    [Fact]
    public void NoticeService_SameMessageTwice_ReplacesTheFirst()
    {
        // Arrange
        Notices.Show(NoticeLevel.Error, "Same");

        // Act
        Notices.Show(NoticeLevel.Error, "Same");

        // Assert
        Assert.Single(Notices.Items);
    }

    [Fact]
    public async Task NoticeService_FailingAction_BecomesAnErrorNoticeAndReturnsTheException()
    {
        // Arrange
        var boom = new ApiException("The name is taken", HttpStatusCode.BadRequest);

        // Act
        var result = await Notices.TryAsync(() => throw boom);

        // Assert
        Assert.Same(boom, result);
        var notice = Assert.Single(Notices.Items);
        Assert.Equal((NoticeLevel.Error, "The name is taken"), (notice.Level, notice.Text));
    }

    [Fact]
    public async Task NoticeService_SucceedingAction_ShowsNothing()
    {
        // Arrange
        // Act
        var result = await Notices.TryAsync(() => Task.CompletedTask);

        // Assert
        Assert.Null(result);
        Assert.Empty(Notices.Items);
    }

    [Fact]
    public void NoticeService_Unauthorized_SaysNothing()
    {
        // Arrange
        var e = new ApiException("Not signed in", HttpStatusCode.Unauthorized);

        // Act
        Notices.ShowFailure(e);

        // Assert
        Assert.Empty(Notices.Items);
    }

    [Fact]
    public void NoticeService_ConflictFromTheUsersOtherTab_SaysSoAndWarns()
    {
        // Arrange
        var e = new TaskConflictException(1, Fmt.HumanServerName, "stale");

        // Act
        var (level, text) = NoticeService.Describe(e);

        // Assert
        Assert.Equal(NoticeLevel.Warning, level);
        Assert.Equal("This task was changed in another tab. Showing the latest version.", text);
    }

    [Fact]
    public void NoticeService_ConflictFromAnAgent_NamesTheAgent()
    {
        // Arrange
        var e = new TaskConflictException(1, "Hermes", "stale");

        // Act
        var (_, text) = NoticeService.Describe(e);

        // Assert
        Assert.Equal("Hermes just changed this. Showing the latest version.", text);
    }

    [Fact]
    public void NoticeService_OfflineFailure_IsAnErrorAboutTheConnection()
    {
        // Arrange
        var e = new ApiException("no route");

        // Act
        var (level, text) = NoticeService.Describe(e);

        // Assert
        Assert.Equal(NoticeLevel.Error, level);
        Assert.Contains("offline", text);
    }

    [Fact]
    public void NoticeService_ServerError_ShowsTheStatusCode()
    {
        // Arrange
        var e = new ApiException("boom", HttpStatusCode.InternalServerError);

        // Act
        var (level, text) = NoticeService.Describe(e);

        // Assert
        Assert.Equal(NoticeLevel.Error, level);
        Assert.Contains("500", text);
    }
}
