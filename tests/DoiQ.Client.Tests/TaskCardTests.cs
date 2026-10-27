using Bunit;
using DoiQ.Client.Components;
using DoiQ.Client.Models;
using Microsoft.AspNetCore.Components.Web;

namespace DoiQ.Client.Tests;

public sealed class TaskCardTests : ClientTestBase
{
    private IRenderedComponent<TaskCard> Card(TaskVm task, Action<ComponentParameterCollectionBuilder<TaskCard>>? more = null) =>
        Render<TaskCard>(p =>
        {
            p.Add(c => c.Task, task);
            more?.Invoke(p);
        });

    [Fact]
    public void TaskCard_Rendered_ShowsTitleAsAButton()
    {
        // Arrange
        var task = Tasks.Make(title: "Renew the domain");

        // Act
        var cut = Card(task);

        // Assert
        Assert.Equal("Renew the domain", cut.Find(".card-title .t").TextContent);
        Assert.Equal("button", cut.Find(".card").GetAttribute("role"));
        Assert.Equal("0", cut.Find(".card").GetAttribute("tabindex"));
    }

    [Fact]
    public void TaskCard_AssignedToTheUser_ShowsYouNotTheServerName()
    {
        // Arrange
        var task = Tasks.Make(assignee: AssigneeVm.UserVm);

        // Act
        var cut = Card(task);

        // Assert
        Assert.Equal("Y", cut.Find(".who .av-user").TextContent);
        Assert.EndsWith("You", cut.Find(".who").TextContent);
        Assert.DoesNotContain(Fmt.HumanServerName, cut.Find(".who").TextContent);
    }

    [Fact]
    public void TaskCard_AssignedToAnAgent_ShowsTheAgentNameAndInitials()
    {
        // Arrange
        var task = Tasks.Make(assignee: AssigneeVm.ForAgent("Hermes 2"));

        // Act
        var cut = Card(task);

        // Assert
        Assert.Contains("Hermes 2", cut.Find(".who").TextContent);
        Assert.Equal("H2", cut.Find(".who .av-agent").TextContent);
    }

    [Fact]
    public void TaskCard_PoolClaimedByAnAgent_NamesTheClaimant()
    {
        // Arrange
        var task = Tasks.Make(assignee: new AssigneeVm(AssigneeKind.Pool, ClaimedBy: "Hermes"));

        // Act
        var cut = Card(task);

        // Assert
        Assert.Contains("Any agent, claimed by Hermes", cut.Find(".who").TextContent);
    }

    [Fact]
    public void TaskCard_Unassigned_SaysUnassigned()
    {
        // Arrange
        var task = Tasks.Make(assignee: AssigneeVm.Unassigned);

        // Act
        var cut = Card(task);

        // Assert
        Assert.Equal("Unassigned", cut.Find(".unassigned").TextContent.Trim());
        Assert.Empty(cut.FindAll(".who"));
    }

    [Fact]
    public void TaskCard_WithADueDate_ShowsItAsIsoDate()
    {
        // Arrange
        var task = Tasks.Make(due: new DateOnly(2099, 10, 27));

        // Act
        var cut = Card(task);

        // Assert
        Assert.Contains("2099-10-27", cut.Find(".due").TextContent);
        Assert.Empty(cut.FindAll(".overdue"));
    }

    [Fact]
    public void TaskCard_Always_ShowsTheCreatedDateAsIsoDate()
    {
        // Arrange
        var task = Tasks.Make();

        // Act
        var cut = Card(task);

        // Assert
        Assert.Contains(Tasks.Now.LocalDateTime.ToString("yyyy-MM-dd"), cut.Find(".created").TextContent);
        Assert.Empty(cut.FindAll(".due"));
    }

    [Fact]
    public void TaskCard_PastDueAndOpen_IsMarkedOverdue()
    {
        // Arrange
        var task = Tasks.Make(lane: BoardLane.Doing, due: new DateOnly(2020, 1, 2));

        // Act
        var cut = Card(task);

        // Assert
        Assert.Contains("2020-01-02", cut.Find(".overdue").TextContent);
    }

    [Fact]
    public void TaskCard_PastDueButDone_IsNotMarkedOverdue()
    {
        // Arrange
        var task = Tasks.Make(lane: BoardLane.Done, due: new DateOnly(2020, 1, 2));

        // Act
        var cut = Card(task);

        // Assert
        Assert.Empty(cut.FindAll(".overdue"));
    }

    [Fact]
    public void TaskCard_WithSubtasks_ShowsDoneOverTotal()
    {
        // Arrange
        var task = Tasks.Make(subtasks: [Tasks.Subtask(1, done: true), Tasks.Subtask(2), Tasks.Subtask(3, done: true), Tasks.Subtask(4)]);

        // Act
        var cut = Card(task);

        // Assert
        var progress = cut.FindAll(".icons > span").Single(s => s.TextContent.Contains("Subtasks done"));
        Assert.Contains("2/4", progress.TextContent);
    }

    [Fact]
    public void TaskCard_WithoutSubtasksFieldsOrComments_ShowsNoCounters()
    {
        // Arrange
        var task = Tasks.Make();

        // Act
        var cut = Card(task);

        // Assert
        Assert.Equal(["created"], cut.Find(".icons").Children.Select(c => c.ClassName));   // only the created date
    }

    [Fact]
    public void TaskCard_WithCommentsAndFields_ShowsBothCounts()
    {
        // Arrange
        var task = Tasks.Make(comments: [Tasks.Comment(1), Tasks.Comment(2), Tasks.Comment(3)], fields: [Tasks.Field(1), Tasks.Field(2)]);

        // Act
        var cut = Card(task);

        // Assert
        var spans = cut.FindAll(".icons > span");
        Assert.Contains(spans, s => s.TextContent.Contains("Comments") && s.TextContent.Contains('3'));
        Assert.Contains(spans, s => s.TextContent.Contains("Info fields") && s.TextContent.Contains('2'));
    }

    [Fact]
    public void TaskCard_Unread_ShowsTheDotAndAScreenReaderLabel()
    {
        // Arrange
        var task = Tasks.Make(unread: true);

        // Act
        var cut = Card(task);

        // Assert
        Assert.NotNull(cut.Find(".unread-dot"));
        Assert.Equal("Unread change", cut.Find(".card-title .sr").TextContent);
    }

    [Fact]
    public void TaskCard_Read_ShowsNoDot()
    {
        // Arrange
        var task = Tasks.Make(unread: false);

        // Act
        var cut = Card(task);

        // Assert
        Assert.Empty(cut.FindAll(".unread-dot"));
    }

    [Fact]
    public void TaskCard_FromEmail_LabelsTheMailIconForScreenReaders()
    {
        // Arrange
        var task = Tasks.Make(fromEmail: true);

        // Act
        var cut = Card(task);

        // Assert
        Assert.Contains(cut.FindAll(".icons .sr"), s => s.TextContent == "From email");
    }

    [Fact]
    public void TaskCard_StateFlags_AreReflectedAsClasses()
    {
        // Arrange
        var task = Tasks.Make(archived: true);

        // Act
        var cut = Card(task, p => { p.Add(c => c.Focused, true); p.Add(c => c.Dragging, true); });

        // Assert
        var classes = cut.Find(".card").ClassList;
        Assert.Contains("is-focused", classes);
        Assert.Contains("is-dragging", classes);
        Assert.Contains("is-archived", classes);
    }

    [Fact]
    public void TaskCard_Clicked_OpensThatTask()
    {
        // Arrange
        var opened = new List<int>();
        var cut = Card(Tasks.Make(id: 42), p => p.Add(c => c.OnOpen, Record(opened)));

        // Act
        cut.Find(".card").Click();

        // Assert
        Assert.Equal([42], opened);
    }

    [Fact]
    public void TaskCard_DragStarted_ReportsItsId()
    {
        // Arrange
        var dragged = new List<int>();
        var cut = Card(Tasks.Make(id: 7), p => p.Add(c => c.OnDragStart, Record(dragged)));

        // Act
        cut.Find(".card").TriggerEvent("ondragstart", new DragEventArgs());

        // Assert
        Assert.Equal([7], dragged);
    }
}
