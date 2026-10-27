using AngleSharp.Dom;
using Bunit;
using DoiQ.Client.Components;
using DoiQ.Client.Models;
using DoiQ.Client.Pages;

namespace DoiQ.Client.Tests;

/// <summary>@mentions in the drawer and the dispatcher on the "Any agent" choice.</summary>
public sealed class DrawerMentionTests : ClientTestBase
{
    private IRenderedComponent<TaskDrawer> RenderDrawer(TaskVm task) => Render<TaskDrawer>(p => p.Add(d => d.Task, task));

    [Fact]
    public void TaskDrawer_MentionButton_PutsTheMentionInTheReply()
    {
        // Arrange
        var cut = RenderDrawer(Tasks.Make());

        // Act
        cut.Find("button[aria-label='Mention Hermes 2']").Click();

        // Assert
        Assert.Equal("@Hermes 2 ", cut.Find("#reply").GetAttribute("value"));
    }

    [Fact]
    public void TaskDrawer_MentionButtonTwice_MentionsOnce()
    {
        // Arrange
        var cut = RenderDrawer(Tasks.Make());
        cut.Find("button[aria-label='Mention Hermes']").Click();

        // Act
        cut.Find("button[aria-label='Mention Hermes']").Click();

        // Assert
        Assert.Equal("@Hermes ", cut.Find("#reply").GetAttribute("value"));
    }

    [Fact]
    public void TaskDrawer_CommentWithUnansweredMention_SaysTheAgentWasAsked()
    {
        // Arrange
        var asking = Tasks.Comment(1, Fmt.HumanServerName, "@Hermes 2 thoughts?") with { Mentions = [new MentionDto(3, "Hermes 2", false)] };

        // Act
        var cut = RenderDrawer(Tasks.Make(comments: [asking]));

        // Assert
        Assert.Equal("Asked Hermes 2 (waiting)", Normalise(cut.Find(".comment .mentions")));
    }

    [Fact]
    public void TaskDrawer_MentionedAgentCommentedSince_ShowsTheMentionAsAnswered()
    {
        // Arrange
        var asking = Tasks.Comment(1, Fmt.HumanServerName, "@Hermes 2 thoughts?") with { Mentions = [new MentionDto(3, "Hermes 2", false)] };
        var answer = Tasks.Comment(2, "Hermes 2", "Looks fine.");

        // Act
        var cut = RenderDrawer(Tasks.Make(comments: [asking, answer]));

        // Assert
        Assert.Equal("Asked Hermes 2 (answered)", Normalise(cut.Find(".comment .mentions")));
    }

    [Fact]
    public async Task TaskDrawer_WithADispatcher_NamesItOnAnyAgent()
    {
        // Arrange
        await Api.UpdateAgentAsync(1, new UpdateAgentRequest("Hermes", IsTriage: true, IsDispatcher: true));

        // Act
        var cut = RenderDrawer(Tasks.Make());

        // Assert
        Assert.Equal("via Hermes", cut.Find(".seg .via").TextContent);
    }

    [Fact]
    public void TaskDrawer_WithoutADispatcher_ShowsPlainAnyAgent()
    {
        // Arrange
        // Act
        var cut = RenderDrawer(Tasks.Make());

        // Assert
        Assert.Empty(cut.FindAll(".seg .via"));
    }

    private static string Normalise(IElement e) => string.Join(' ', e.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Replace(" ,", ",");
}

/// <summary>Settings: the dispatcher flag and asking an agent to describe its responsibilities.</summary>
public sealed class SettingsAgentTests : ClientTestBase
{
    private static IElement Card(IRenderedComponent<Settings> cut, string agent) =>
        cut.FindAll("section[aria-labelledby=agents-h] article.agent-card").Single(c => c.QuerySelector("h4")?.TextContent == agent);

    [Fact]
    public async Task Settings_AskButton_AsksThatAgent()
    {
        // Arrange
        var cut = Render<Settings>();
        await cut.WaitForStateAsync(() => cut.FindAll("section[aria-labelledby=agents-h] article.agent-card").Count == 2, TimeSpan.FromSeconds(5));

        // Act
        Card(cut, "Hermes").QuerySelectorAll("button").Single(b => b.TextContent == "Ask it to describe itself").Click();

        // Assert
        Assert.Equal([1], Api.ResponsibilityAsks);
    }

    [Fact]
    public async Task Settings_AgentAsked_SaysItIsWaitingForTheAnswer()
    {
        // Arrange
        var cut = Render<Settings>();
        await cut.WaitForStateAsync(() => cut.FindAll("section[aria-labelledby=agents-h] article.agent-card").Count == 2, TimeSpan.FromSeconds(5));

        // Act
        Card(cut, "Hermes").QuerySelectorAll("button").Single(b => b.TextContent == "Ask it to describe itself").Click();

        // Assert
        cut.WaitForAssertion(() => Assert.Equal("Asked; waiting for the agent to answer", Card(cut, "Hermes").QuerySelector("dd.resp")!.TextContent));
    }

    [Fact]
    public async Task Settings_AgentWithResponsibilities_ShowsThem()
    {
        // Arrange
        // Act
        var cut = Render<Settings>();
        await cut.WaitForStateAsync(() => cut.FindAll("section[aria-labelledby=agents-h] article.agent-card").Count == 2, TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal("Content: newsletters, blog posts and social copy.", Card(cut, "Hermes 2").QuerySelector("dd.resp")!.TextContent);
    }

    [Fact]
    public async Task Settings_AddAgentWithAskTicked_AsksTheNewAgent()
    {
        // Arrange
        var cut = Render<Settings>();
        await cut.WaitForStateAsync(() => cut.FindAll("section[aria-labelledby=agents-h] article.agent-card").Count == 2, TimeSpan.FromSeconds(5));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Add agent")).Click();
        cut.Find("#af-0-name").Input("Ada");

        // Act
        cut.Find("form.agent-form").Submit();

        // Assert
        cut.WaitForAssertion(() => Assert.Equal([3], Api.ResponsibilityAsks));
    }

    [Fact]
    public async Task Settings_DispatcherTicked_ShowsTheDispatcherBadge()
    {
        // Arrange
        var cut = Render<Settings>();
        await cut.WaitForStateAsync(() => cut.FindAll("section[aria-labelledby=agents-h] article.agent-card").Count == 2, TimeSpan.FromSeconds(5));
        Card(cut, "Hermes 2").QuerySelectorAll("button").Single(b => b.TextContent == "Edit").Click();
        Card(cut, "Hermes 2").QuerySelectorAll("input[type=checkbox]")[1].Change(true);

        // Act
        cut.Find("form.agent-form").Submit();

        // Assert
        cut.WaitForAssertion(() => Assert.Contains("Dispatcher", Card(cut, "Hermes 2").QuerySelector("header")!.TextContent));
    }
}
