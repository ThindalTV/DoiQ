using Bunit;
using DoiQ.Client.Components;
using DoiQ.Client.Models;
using Microsoft.AspNetCore.Components.Web;

namespace DoiQ.Client.Tests;

public sealed class TaskDrawerTests : ClientTestBase
{
    private IRenderedComponent<TaskDrawer> RenderDrawer(TaskVm task, Action<ComponentParameterCollectionBuilder<TaskDrawer>>? more = null) =>
        Render<TaskDrawer>(p =>
        {
            p.Add(d => d.Task, task);
            more?.Invoke(p);
        });

    private async Task<TaskVm> SeededTask(int id) => (await Api.GetBoard(Ct)).Single(t => t.Id == id);

    [Fact]
    public void TaskDrawer_Opened_ShowsIdLaneAndWhoUpdatedItAsYou()
    {
        // Arrange
        var task = Tasks.Make(id: 17, lane: BoardLane.Doing, updatedBy: Fmt.HumanServerName);

        // Act
        var cut = RenderDrawer(task);

        // Assert
        var head = cut.Find(".drawer-head").TextContent;
        Assert.Contains("#17", head);
        Assert.Contains("Doing", head);
        Assert.Contains("Updated by You", head);
        Assert.DoesNotContain("Updated by User", head);
    }

    [Fact]
    public void TaskDrawer_Opened_ShowsTitleAndStatusAsFields()
    {
        // Arrange
        var task = Tasks.Make(title: "Reply to the venue", lane: BoardLane.Waiting, due: new DateOnly(2026, 10, 27));

        // Act
        var cut = RenderDrawer(task);

        // Assert
        Assert.Equal("Reply to the venue", cut.Find("#task-title").GetAttribute("value"));
        Assert.Equal("Waiting", cut.Find("#task-status option[selected]").TextContent);
        Assert.Equal("2026-10-27", cut.Find("#task-due").GetAttribute("value"));
    }

    [Fact]
    public void TaskDrawer_Description_RendersMarkdown()
    {
        // Arrange
        var task = Tasks.Make(description: "Confirm the **date** and\n\n- headcount\n- AV");

        // Act
        var cut = RenderDrawer(task);

        // Assert
        Assert.Equal("date", cut.Find(".md strong").TextContent);
        Assert.Equal(2, cut.FindAll(".md li").Count);
    }

    [Fact]
    public void TaskDrawer_Description_RendersPipeTables()
    {
        // Arrange
        var task = Tasks.Make(description: "| Time | Voiceover |\n|---|---|\n| 0–5s | Intro |\n| 5–14s | **Join** |");

        // Act
        var cut = RenderDrawer(task);

        // Assert
        Assert.Equal(["Time", "Voiceover"], cut.FindAll(".md th").Select(th => th.TextContent));
        Assert.Equal(2, cut.FindAll(".md tbody tr").Count);
        Assert.Equal("Join", cut.Find(".md td strong").TextContent);
    }

    [Fact]
    public void TaskDrawer_Description_NeverRendersRawHtml()
    {
        // Arrange
        var task = Tasks.Make(description: "Hi <script>alert(1)</script><img src=x onerror=alert(1)><b onclick=\"x()\">bold</b>");

        // Act
        var cut = RenderDrawer(task);

        // Assert
        var md = cut.Find("section[aria-labelledby=desc-h] .md");
        Assert.Empty(md.QuerySelectorAll("script, img, b"));
        Assert.Empty(md.QuerySelectorAll("[onerror], [onclick]"));
        Assert.Contains("&lt;script&gt;", md.InnerHtml);
    }

    [Theory]
    [InlineData("[click me](javascript:alert(1))")]
    [InlineData("[click me](java\tscript:alert(1))")]
    [InlineData("![img](javascript:alert(1))")]
    [InlineData("<javascript:alert(1)>")]
    [InlineData("[click me](JaVaScRiPt:alert(1))")]
    [InlineData("[click me](data:text/html;base64,PHNjcmlwdD4=)")]
    [InlineData("[click me](vbscript:msgbox(1))")]
    public void TaskDrawer_Description_DoesNotKeepScriptedLinks(string markdown)
    {
        // Arrange
        var task = Tasks.Make(description: markdown);

        // Act
        var cut = RenderDrawer(task);

        // Assert
        var hrefs = cut.FindAll(".md a").Select(a => a.GetAttribute("href") ?? "").ToList();
        Assert.DoesNotContain(hrefs, h => h.Contains("script", StringComparison.OrdinalIgnoreCase) || h.StartsWith("data:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TaskDrawer_Description_KeepsOrdinaryLinks()
    {
        // Arrange
        var task = Tasks.Make(description: "See [the docs](https://example.com/docs) and https://example.org");

        // Act
        var cut = RenderDrawer(task);

        // Assert
        var hrefs = cut.FindAll(".md a").Select(a => a.GetAttribute("href")).ToList();
        Assert.Equal(["https://example.com/docs", "https://example.org"], hrefs);
    }

    [Fact]
    public void TaskDrawer_NoDescription_SaysSo()
    {
        // Arrange
        var task = Tasks.Make(description: "  ");

        // Act
        var cut = RenderDrawer(task);

        // Assert
        Assert.Equal("No description yet.", cut.Find("section[aria-labelledby=desc-h] p").TextContent);
    }

    [Fact]
    public async Task TaskDrawer_EditDescription_SavesTheDraftThroughTheApi()
    {
        // Arrange
        var cut = RenderDrawer(await SeededTask(1));
        cut.FindAll("section[aria-labelledby=desc-h] button").Single(b => b.TextContent == "Edit").Click();
        cut.Find("#desc-edit").Input("New **text**");

        // Act
        cut.FindAll("section[aria-labelledby=desc-h] button").Single(b => b.TextContent == "Save").Click();

        // Assert
        Assert.Equal("New **text**", (await SeededTask(1)).Description);
        Assert.Empty(cut.FindAll("#desc-edit"));
    }

    [Fact]
    public async Task TaskDrawer_TickingASubtask_MarksItDoneThroughTheApiAndRaisesChanged()
    {
        // Arrange
        var changed = 0;
        var task = await SeededTask(42);
        var cut = RenderDrawer(task, p => p.Add(d => d.OnChanged, Count(() => changed++)));
        Assert.False((await SeededTask(42)).Subtasks.Single(s => s.Id == 3).Done);

        // Act
        cut.Find("#st-3").Change(true);

        // Assert
        Assert.True((await SeededTask(42)).Subtasks.Single(s => s.Id == 3).Done);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task TaskDrawer_WithSubtasks_ShowsProgressAndAssignees()
    {
        // Arrange
        var task = await SeededTask(42);

        // Act
        var cut = RenderDrawer(task);

        // Assert
        Assert.Contains("2 of 4", cut.Find("section[aria-labelledby=subs-h] .sec-head").TextContent);
        var items = cut.FindAll("ul.subtasks li:not(.add)");
        Assert.Equal(4, items.Count);
        Assert.Contains("You", items[2].TextContent);
        Assert.Contains("Hermes", items[0].TextContent);
        Assert.Contains("done", items[0].ClassList);
    }

    [Fact]
    public async Task TaskDrawer_NewSubtaskTitleAndEnter_AddsItThroughTheApi()
    {
        // Arrange
        var task = await SeededTask(1);
        var cut = RenderDrawer(task);
        var box = cut.Find("#st-new");
        box.Input("Check the NAS");

        // Act
        box.KeyDown(new KeyboardEventArgs { Key = "Enter" });

        // Assert
        Assert.Equal("Check the NAS", (await SeededTask(1)).Subtasks.Single().Title);
    }

    [Fact]
    public void TaskDrawer_Fields_ShowNameBodyAndAuthorAsYou()
    {
        // Arrange
        var field = new InfoFieldVm(1, "Notes", "Ask about **projector**", Fmt.HumanServerName, Tasks.Now, false);
        var task = Tasks.Make(fields: [field]);

        // Act
        var cut = RenderDrawer(task);

        // Assert
        var card = cut.Find("article.field-card");
        Assert.Equal("Notes", card.QuerySelector("h4")!.TextContent);
        Assert.StartsWith("You", card.QuerySelector(".by")!.TextContent);
        Assert.Equal("projector", card.QuerySelector(".md strong")!.TextContent);
        Assert.Empty(card.QuerySelectorAll(".badge-new"));
    }

    [Fact]
    public void TaskDrawer_NewField_IsBadgedNew()
    {
        // Arrange
        var task = Tasks.Make(fields: [Tasks.Field(1, isNew: true), Tasks.Field(2)]);

        // Act
        var cut = RenderDrawer(task);

        // Assert
        var cards = cut.FindAll("article.field-card");
        Assert.Contains("is-new", cards[0].ClassList);
        Assert.Single(cards[0].QuerySelectorAll(".badge-new"));
        Assert.DoesNotContain("is-new", cards[1].ClassList);
    }

    [Fact]
    public async Task TaskDrawer_AddingAField_SavesNameAndBodyThroughTheApi()
    {
        // Arrange
        var task = await SeededTask(1);
        var cut = RenderDrawer(task);
        cut.FindAll("button").Single(b => b.TextContent.Contains("Add field")).Click();
        cut.Find("#field-name").Input("Deadline");
        cut.Find("#field-body").Input("27 Oct");

        // Act
        cut.FindAll(".field-card button").Single(b => b.TextContent == "Save").Click();

        // Assert
        var saved = Assert.Single((await SeededTask(1)).Fields);
        Assert.Equal(("Deadline", "27 Oct"), (saved.Name, saved.Body));
    }

    [Fact]
    public void TaskDrawer_Comments_ShowAuthorsWithTheUserAsYou()
    {
        // Arrange
        var task = Tasks.Make(comments: [Tasks.Comment(1, "Hermes", "Draft is ready"), Tasks.Comment(2, Fmt.HumanServerName, "Thanks")]);

        // Act
        var cut = RenderDrawer(task);

        // Assert
        var authors = cut.FindAll(".comment .head strong").Select(s => s.TextContent);
        Assert.Equal(["Hermes", "You"], authors);
    }

    [Fact]
    public void TaskDrawer_NewComments_GetABadgeAndOneSeparatorBeforeTheFirst()
    {
        // Arrange
        var task = Tasks.Make(comments:
        [
            Tasks.Comment(1, "Hermes", "Old"),
            Tasks.Comment(2, "Hermes", "Fresh one", isNew: true),
            Tasks.Comment(3, "Hermes", "Fresh two", isNew: true),
        ]);

        // Act
        var cut = RenderDrawer(task);

        // Assert
        var separator = Assert.Single(cut.FindAll("[role=separator]"));
        Assert.Contains("New since you looked", separator.TextContent);
        var comments = cut.FindAll(".comment");
        Assert.Empty(comments[0].QuerySelectorAll(".badge-new"));
        Assert.Single(comments[1].QuerySelectorAll(".badge-new"));
        Assert.Single(comments[2].QuerySelectorAll(".badge-new"));
        Assert.Equal(separator.NextElementSibling, comments[1]);
    }

    [Fact]
    public void TaskDrawer_NoNewComments_ShowsNoSeparator()
    {
        // Arrange
        var task = Tasks.Make(comments: [Tasks.Comment(1), Tasks.Comment(2)]);

        // Act
        var cut = RenderDrawer(task);

        // Assert
        Assert.Empty(cut.FindAll("[role=separator]"));
        Assert.Empty(cut.FindAll(".comment .badge-new"));
    }

    [Fact]
    public void TaskDrawer_MarkersSurviveTheTaskBeingMarkedSeen()
    {
        // Arrange
        var fresh = Tasks.Make(id: 5, comments: [Tasks.Comment(1, isNew: true)]);
        var cut = RenderDrawer(fresh);

        // Act
        cut.Render(p => p.Add(d => d.Task, fresh with { Comments = [Tasks.Comment(1, isNew: false)] }));

        // Assert
        Assert.Single(cut.FindAll("[role=separator]"));
        Assert.Single(cut.FindAll(".comment .badge-new"));
    }

    [Fact]
    public void TaskDrawer_CommentSeenByAgents_ShowsAReceiptOnTheUsersCommentOnly()
    {
        // Arrange
        var task = Tasks.Make(
            comments: [Tasks.Comment(1, Fmt.HumanServerName, "Please do", eventId: 10), Tasks.Comment(2, "Hermes", "On it", eventId: 11)],
            seenBy: [new SeenByVm("Hermes", 11), new SeenByVm("Hermes 2", 10)]);

        // Act
        var cut = RenderDrawer(task);

        // Assert
        var comments = cut.FindAll(".comment");
        Assert.Equal("Seen by Hermes, Hermes 2", comments[0].QuerySelector(".receipt")!.TextContent);
        Assert.Null(comments[1].QuerySelector(".receipt"));
    }

    [Fact]
    public void TaskDrawer_CommentNobodyHasSeen_ShowsNoReceipt()
    {
        // Arrange
        var task = Tasks.Make(
            comments: [Tasks.Comment(1, Fmt.HumanServerName, "Please do", eventId: 10)],
            seenBy: [new SeenByVm("Hermes", 9)]);

        // Act
        var cut = RenderDrawer(task);

        // Assert
        Assert.Empty(cut.FindAll(".receipt"));
    }

    [Fact]
    public void TaskDrawer_ReceiptNamingTheUser_ShowsYou()
    {
        // Arrange
        var task = Tasks.Make(
            comments: [Tasks.Comment(1, Fmt.HumanServerName, "Hello", eventId: 10)],
            seenBy: [new SeenByVm(Fmt.HumanServerName, 10)]);

        // Act
        var cut = RenderDrawer(task);

        // Assert
        Assert.Equal("Seen by You", cut.Find(".receipt").TextContent);
    }

    [Fact]
    public async Task TaskDrawer_SendingAComment_AddsItThroughTheApiAndClearsTheBox()
    {
        // Arrange
        var task = await SeededTask(1);
        var cut = RenderDrawer(task);
        cut.Find("#reply").Input("Looks good");

        // Act
        cut.FindAll(".reply button").Single(b => b.TextContent == "Send").Click();

        // Assert
        var comment = Assert.Single((await SeededTask(1)).Comments);
        Assert.Equal(("Looks good", Fmt.HumanServerName), (comment.Body, comment.Author));
        Assert.Equal("", cut.Find("#reply").GetAttribute("value") ?? "");
    }

    [Fact]
    public async Task TaskDrawer_CtrlEnterInTheReplyBox_SendsTheComment()
    {
        // Arrange
        var task = await SeededTask(1);
        var cut = RenderDrawer(task);
        var box = cut.Find("#reply");
        box.Input("Sent by keyboard");

        // Act
        box.KeyDown(new KeyboardEventArgs { Key = "Enter", CtrlKey = true });

        // Assert
        Assert.Single((await SeededTask(1)).Comments);
    }

    [Fact]
    public async Task TaskDrawer_SendingAnEmptyComment_DoesNothing()
    {
        // Arrange
        var task = await SeededTask(1);
        var cut = RenderDrawer(task);
        cut.Find("#reply").Input("   ");

        // Act
        cut.FindAll(".reply button").Single(b => b.TextContent == "Send").Click();

        // Assert
        Assert.Empty((await SeededTask(1)).Comments);
    }

    [Fact]
    public void TaskDrawer_AssignedToAnAgent_OffersGiveAndTakeIt()
    {
        // Arrange
        var task = Tasks.Make(assignee: AssigneeVm.ForAgent("Hermes"));

        // Act
        var cut = RenderDrawer(task);

        // Assert
        var labels = cut.FindAll(".drawer-foot button").Select(b => b.TextContent);
        Assert.Contains(labels, l => l.StartsWith("Give to agent"));
        Assert.Contains(labels, l => l.StartsWith("Take it"));
    }

    [Fact]
    public void TaskDrawer_AssignedToTheUser_HidesTakeIt()
    {
        // Arrange
        var task = Tasks.Make(assignee: AssigneeVm.UserVm);

        // Act
        var cut = RenderDrawer(task);

        // Assert
        Assert.DoesNotContain(cut.FindAll(".drawer-foot button"), b => b.TextContent.StartsWith("Take it"));
        Assert.Contains(cut.FindAll(".drawer-foot button"), b => b.TextContent.StartsWith("Give to agent"));
    }

    [Fact]
    public void TaskDrawer_GiveToAgentAndTakeIt_RaiseTheirCallbacks()
    {
        // Arrange
        var given = 0;
        var taken = 0;
        var cut = RenderDrawer(Tasks.Make(assignee: AssigneeVm.Unassigned), p =>
        {
            p.Add(d => d.OnGive, Count(() => given++));
            p.Add(d => d.OnTake, Count(() => taken++));
        });

        // Act
        cut.FindAll(".drawer-foot button").Single(b => b.TextContent.StartsWith("Give to agent")).Click();
        cut.FindAll(".drawer-foot button").Single(b => b.TextContent.StartsWith("Take it")).Click();

        // Assert
        Assert.Equal((1, 1), (given, taken));
    }

    [Fact]
    public void TaskDrawer_AssigneeButtons_RaiseTheChosenAssignee()
    {
        // Arrange
        var assigned = new List<AssigneeVm>();
        var cut = RenderDrawer(Tasks.Make(), p => p.Add(d => d.OnAssign, Record(assigned)));

        // Act
        cut.FindAll("[aria-labelledby=assignee-label] button").Single(b => b.TextContent.Contains("Any agent")).Click();
        cut.FindAll("[aria-labelledby=assignee-label] button").Single(b => b.TextContent.Contains("You")).Click();

        // Assert
        Assert.Equal([AssigneeVm.AnyAgent, AssigneeVm.UserVm], assigned);
    }

    [Fact]
    public void TaskDrawer_AgentButton_ShowsTheLastAgentUsedWhenNoneIsAssigned()
    {
        // Arrange
        // Act
        var cut = RenderDrawer(Tasks.Make(), p => p.Add(d => d.LastAgent, "Hermes 2"));

        // Assert
        Assert.Contains("Agent: Hermes 2", cut.Find("[aria-haspopup=dialog]").TextContent);
    }

    [Fact]
    public void TaskDrawer_MoveButton_NamesTheNextLaneAndRaisesIt()
    {
        // Arrange
        var moves = new List<BoardLane>();
        var cut = RenderDrawer(Tasks.Make(lane: BoardLane.Waiting), p => p.Add(d => d.OnMoveTo, Record(moves)));

        // Act
        cut.FindAll(".drawer-foot button").Single(b => b.TextContent.StartsWith("Move to")).Click();

        // Assert
        Assert.Equal([BoardLane.Doing], moves);
    }

    [Fact]
    public void TaskDrawer_InTheLastLane_OffersNoMove()
    {
        // Arrange
        // Act
        var cut = RenderDrawer(Tasks.Make(lane: BoardLane.Done));

        // Assert
        Assert.DoesNotContain(cut.FindAll(".drawer-foot button"), b => b.TextContent.StartsWith("Move to"));
    }

    [Fact]
    public void TaskDrawer_DoneTask_OffersNoManualArchive()
    {
        // Arrange
        var task = Tasks.Make(lane: BoardLane.Done);

        // Act
        var cut = RenderDrawer(task);

        // Assert
        Assert.DoesNotContain(cut.FindAll(".drawer-foot button"), b => b.TextContent.Contains("rchive"));
    }

    [Fact]
    public async Task TaskDrawer_Delete_NeedsAConfirmationClick()
    {
        // Arrange
        var deleted = 0;
        var cut = RenderDrawer(await SeededTask(3), p => p.Add(d => d.OnDeleted, Count(() => deleted++)));

        // Act
        cut.FindAll(".drawer-foot button").Single(b => b.TextContent == "Delete task").Click();

        // Assert
        Assert.Equal(0, deleted);
        Assert.Contains(await Api.GetBoard(Ct), t => t.Id == 3);
        Assert.Contains(cut.FindAll(".drawer-foot button"), b => b.TextContent == "Confirm delete");
    }

    [Fact]
    public async Task TaskDrawer_ConfirmDelete_DeletesThroughTheApiAndRaisesOnDeleted()
    {
        // Arrange
        var deleted = 0;
        var cut = RenderDrawer(await SeededTask(3), p => p.Add(d => d.OnDeleted, Count(() => deleted++)));
        cut.FindAll(".drawer-foot button").Single(b => b.TextContent == "Delete task").Click();

        // Act
        cut.FindAll(".drawer-foot button").Single(b => b.TextContent == "Confirm delete").Click();

        // Assert
        Assert.DoesNotContain(await Api.GetBoard(Ct), t => t.Id == 3);
        Assert.Equal(1, deleted);
    }

    [Fact]
    public async Task TaskDrawer_KeepAfterDelete_BacksOut()
    {
        // Arrange
        var cut = RenderDrawer(await SeededTask(3));
        cut.FindAll(".drawer-foot button").Single(b => b.TextContent == "Delete task").Click();

        // Act
        cut.FindAll(".drawer-foot button").Single(b => b.TextContent == "Keep").Click();

        // Assert
        Assert.Contains(cut.FindAll(".drawer-foot button"), b => b.TextContent == "Delete task");
        Assert.Contains(await Api.GetBoard(Ct), t => t.Id == 3);
    }

    [Fact]
    public void TaskDrawer_CloseButtonAndScrim_RaiseOnClose()
    {
        // Arrange
        var closed = 0;
        var cut = RenderDrawer(Tasks.Make(), p => p.Add(d => d.OnClose, Count(() => closed++)));

        // Act
        cut.Find("button.close").Click();
        cut.Find(".scrim").Click();

        // Assert
        Assert.Equal(2, closed);
        Assert.Equal("Task details", cut.Find("aside").GetAttribute("aria-label"));
        Assert.Equal("dialog", cut.Find("aside").GetAttribute("role"));
    }
}
