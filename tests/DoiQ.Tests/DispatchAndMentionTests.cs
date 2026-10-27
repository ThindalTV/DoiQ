using DoiQ.Core.Domain;
using DoiQ.Core.Services;
using DoiQ.Shared.Dtos;
using DoiQ.Shared.Enums;
using Microsoft.EntityFrameworkCore;

namespace DoiQ.Tests;

/// <summary>The dispatcher: "any agent" tasks go to it, and it passes each one to the agent best placed to do it.</summary>
public class DispatcherTests
{
    /// <summary>Hermes (also the triage agent) becomes the dispatcher; Odin is the agent it hands work to.</summary>
    private static async Task MakeHermesDispatcherAsync(Harness h)
    {
        await using var db = h.NewDb();
        await db.Actors.Where(a => a.Id == h.Hermes.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsDispatcher, true), h.Ct);
    }

    private static async Task<List<OutboxMessage>> TodosAsync(Harness h)
    {
        await using var db = h.NewDb();
        return await db.Outbox.AsNoTracking().Where(o => o.Kind == OutboxKind.Todo).ToListAsync(h.Ct);
    }

    [Fact]
    public async Task AssignAsync_UserGivesTaskToAnyAgent_WakesOnlyTheDispatcher()
    {
        // Arrange
        using var h = new Harness();
        await MakeHermesDispatcherAsync(h);
        var id = await h.CreateAsync(h.User, "Write the newsletter");

        // Act
        await h.AsUser().AssignAsync(id, new AssignRequest("any"), h.Ct);

        // Assert
        Assert.Equal([h.Hermes.Id], (await TodosAsync(h)).Select(o => o.TargetActorId));
    }

    [Fact]
    public async Task AssignAsync_UserGivesTaskToAnyAgentWithoutDispatcher_WakesEveryAgent()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "Write the newsletter");

        // Act
        await h.AsUser().AssignAsync(id, new AssignRequest("any"), h.Ct);

        // Assert
        Assert.Equal([h.Hermes.Id, h.Odin.Id], (await TodosAsync(h)).Select(o => o.TargetActorId).Order());
    }

    [Fact]
    public async Task GetInboxAsync_AnyAgentTask_IsDispatchForTheDispatcher()
    {
        // Arrange
        using var h = new Harness();
        await MakeHermesDispatcherAsync(h);
        await h.CreateAsync(h.User, "Write the newsletter", assignee: "any");

        // Act
        var inbox = await h.AsHermes().GetInboxAsync(h.Ct);

        // Assert
        Assert.Equal("Dispatch", Assert.Single(inbox).Reason);
    }

    [Fact]
    public async Task GetInboxAsync_AnyAgentTaskForAnotherAgent_IsEmptyWhileThereIsADispatcher()
    {
        // Arrange
        using var h = new Harness();
        await MakeHermesDispatcherAsync(h);
        await h.CreateAsync(h.User, "Write the newsletter", assignee: "any");

        // Act
        var inbox = await h.AsOdin().GetInboxAsync(h.Ct);

        // Assert
        Assert.Empty(inbox);
    }

    [Fact]
    public async Task ClaimAsync_AnotherAgentWhileThereIsADispatcher_IsForbidden()
    {
        // Arrange
        using var h = new Harness();
        await MakeHermesDispatcherAsync(h);
        var id = await h.CreateAsync(h.User, "Write the newsletter", assignee: "any");

        // Act
        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => h.AsOdin().ClaimAsync(id, h.Ct));

        // Assert
        Assert.Contains("Hermes dispatches", ex.Message);
    }

    [Fact]
    public async Task AssignAsync_DispatcherPassesTaskOn_WakesTheChosenAgent()
    {
        // Arrange
        using var h = new Harness();
        await MakeHermesDispatcherAsync(h);
        var id = await h.CreateAsync(h.User, "Write the newsletter", assignee: "any");
        await using (var db = h.NewDb()) await db.Outbox.ExecuteDeleteAsync(h.Ct);

        // Act
        await h.AsHermes().AssignAsync(id, new AssignRequest("Odin"), h.Ct);

        // Assert
        Assert.Equal([h.Odin.Id], (await TodosAsync(h)).Select(o => o.TargetActorId));
    }

    [Fact]
    public async Task AssignAsync_DispatcherPassesTaskOn_PutsItInTheChosenAgentsInboxInWaiting()
    {
        // Arrange
        using var h = new Harness();
        await MakeHermesDispatcherAsync(h);
        var id = await h.CreateAsync(h.User, "Write the newsletter", assignee: "any");

        // Act
        await h.AsHermes().AssignAsync(id, new AssignRequest("Odin"), h.Ct);

        // Assert
        var item = Assert.Single(await h.AsOdin().GetInboxAsync(h.Ct));
        Assert.Equal(("Assigned", BoardStatus.Waiting), (item.Reason, item.Task.Status));
    }

    [Fact]
    public async Task GetInboxAsync_TaskHandledEarlierThenAssignedByAnotherAgent_IsBackAsAssigned()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "Write the newsletter", BoardStatus.Waiting, "Odin");
        await h.AsOdin().MarkHandled(id, null, h.Ct);
        await h.AsUser().AssignAsync(id, new AssignRequest("Hermes"), h.Ct);

        // Act
        await h.AsHermes().AssignAsync(id, new AssignRequest("Odin"), h.Ct);

        // Assert
        Assert.Equal("Assigned", Assert.Single(await h.AsOdin().GetInboxAsync(h.Ct)).Reason);
    }

    [Fact]
    public async Task AssignAsync_DispatcherTakesTaskItself_ListsItAsAssignedToIt()
    {
        // Arrange
        using var h = new Harness();
        await MakeHermesDispatcherAsync(h);
        var id = await h.CreateAsync(h.User, "Book the venue", assignee: "any");

        // Act
        await h.AsHermes().AssignAsync(id, new AssignRequest("Hermes"), h.Ct);

        // Assert
        Assert.Equal("Assigned", Assert.Single(await h.AsHermes().GetInboxAsync(h.Ct)).Reason);
    }

    [Fact]
    public async Task UpdateAsync_SecondDispatcher_TakesTheFlagFromTheFirst()
    {
        // Arrange
        using var h = new Harness();
        await MakeHermesDispatcherAsync(h);
        var admin = new AgentAdminService(h.NewDb(), h.Clock);

        // Act
        await admin.UpdateAsync(h.Odin.Id, new UpdateAgentRequest("Odin", IsDispatcher: true), h.Ct);

        // Assert
        var dispatchers = (await new AgentAdminService(h.NewDb(), h.Clock).ListAsync(h.Ct)).Where(a => a.IsDispatcher).Select(a => a.Name);
        Assert.Equal(["Odin"], dispatchers);
    }
}

/// <summary>@mentions: asking an actor for input on a task without handing it over.</summary>
public class MentionTests
{
    private static Task<CommentDto> CommentAsync(Harness h, ActorInfo by, int taskId, string body) =>
        h.As(by).AddComment(taskId, new AddCommentRequest(body), h.Ct);

    [Fact]
    public async Task AddComment_UserMentionsAgent_WakesItAsMentioned()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "Plan the launch", BoardStatus.Waiting, "Hermes");
        await using (var db0 = h.NewDb()) await db0.Outbox.ExecuteDeleteAsync(h.Ct);

        // Act
        await CommentAsync(h, h.User, id, "@Odin what do you think of the copy?");

        // Assert
        await using var db = h.NewDb();
        var odin = Assert.Single(db.Outbox.Where(o => o.TargetActorId == h.Odin.Id));
        Assert.Equal(TaskService.MentionedReason, odin.PayloadJson);
    }

    [Fact]
    public async Task AddComment_UserMentionsAgentThatIsTheAssignee_QueuesOneWakeUp()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "Plan the launch", BoardStatus.Waiting, "Hermes");
        await using (var db0 = h.NewDb()) await db0.Outbox.ExecuteDeleteAsync(h.Ct);

        // Act
        await CommentAsync(h, h.User, id, "@Hermes any news?");

        // Assert
        await using var db = h.NewDb();
        Assert.Single(db.Outbox.Where(o => o.TargetActorId == h.Hermes.Id));
    }

    [Theory]
    [InlineData("mail odin@example.com about it")]
    [InlineData("@Odinson will know")]
    [InlineData("no mention here")]
    public async Task AddComment_TextThatOnlyLooksLikeAMention_MentionsNobody(string body)
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "Plan the launch");

        // Act
        var c = await CommentAsync(h, h.User, id, body);

        // Assert
        Assert.Null(c.Mentions);
    }

    [Fact]
    public async Task AddComment_MentionInAnyCase_MentionsTheActor()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "Plan the launch");

        // Act
        var c = await CommentAsync(h, h.User, id, "Thoughts, @odin?");

        // Assert
        Assert.Equal(["Odin"], c.Mentions!.Select(m => m.Name));
    }

    [Fact]
    public async Task GetInboxAsync_MentionedAgent_ListsTaskAsMentioned()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "Plan the launch", BoardStatus.Waiting, "Hermes");
        await CommentAsync(h, h.User, id, "@Odin what do you think?");

        // Act
        var inbox = await h.AsOdin().GetInboxAsync(h.Ct);

        // Assert
        Assert.Equal("Mentioned", Assert.Single(inbox).Reason);
    }

    [Fact]
    public async Task AddComment_MentionedAgentAnswers_LeavesItsInbox()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "Plan the launch", BoardStatus.Waiting, "Hermes");
        await CommentAsync(h, h.User, id, "@Odin what do you think?");

        // Act
        await CommentAsync(h, h.Odin, id, "Looks good to me.");

        // Assert
        Assert.Empty(await h.AsOdin().GetInboxAsync(h.Ct));
    }

    [Fact]
    public async Task GetAsync_MentionAnswered_ShowsTheMentionAsAnswered()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "Plan the launch", BoardStatus.Waiting, "Hermes");
        await CommentAsync(h, h.User, id, "@Odin what do you think?");
        await CommentAsync(h, h.Odin, id, "Looks good to me.");

        // Act
        var task = await h.AsUser().GetAsync(id, h.Ct);

        // Assert
        Assert.Equal(new MentionDto(h.Odin.Id, "Odin", true), Assert.Single(task.Comments[0].Mentions!));
    }

    [Fact]
    public async Task MarkHandled_MentionedAgent_ClosesTheMention()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "Plan the launch", BoardStatus.Waiting, "Hermes");
        await CommentAsync(h, h.User, id, "@Odin what do you think?");

        // Act
        await h.AsOdin().MarkHandled(id, null, h.Ct);

        // Assert
        Assert.True(Assert.Single((await h.AsUser().GetAsync(id, h.Ct)).Comments[0].Mentions!).Answered);
    }

    [Fact]
    public async Task UpsertFieldAsync_MentionedAgentOnTaskAnotherAgentHolds_WritesTheField()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "Plan the launch", BoardStatus.Waiting, "Hermes");
        await CommentAsync(h, h.User, id, "@Odin please add your estimate");

        // Act
        await h.AsOdin().UpsertFieldAsync(id, new FieldRequest("Estimate", "2 days"), h.Ct);

        // Assert
        Assert.Equal("2 days", Assert.Single((await h.AsUser().GetAsync(id, h.Ct)).Fields).Value);
    }

    [Fact]
    public async Task UpsertFieldAsync_AgentNotMentionedOnTaskAnotherAgentHolds_IsForbidden()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "Plan the launch", BoardStatus.Waiting, "Hermes");

        // Act
        var act = () => h.AsOdin().UpsertFieldAsync(id, new FieldRequest("Estimate", "2 days"), h.Ct);

        // Assert
        await Assert.ThrowsAsync<ForbiddenException>(act);
    }

    [Fact]
    public async Task UpsertFieldAsync_AfterTheMentionWasAnswered_IsForbidden()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "Plan the launch", BoardStatus.Waiting, "Hermes");
        await CommentAsync(h, h.User, id, "@Odin please add your estimate");
        await h.AsOdin().UpsertFieldAsync(id, new FieldRequest("Estimate", "2 days"), h.Ct);

        // Act
        var act = () => h.AsOdin().UpsertFieldAsync(id, new FieldRequest("Estimate", "3 days"), h.Ct);

        // Assert
        await Assert.ThrowsAsync<ForbiddenException>(act);
    }

    [Fact]
    public async Task AddComment_AgentMentionsAgent_WakesIt()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "Plan the launch", BoardStatus.Waiting, "Hermes");
        await using (var db0 = h.NewDb()) await db0.Outbox.ExecuteDeleteAsync(h.Ct);

        // Act
        await CommentAsync(h, h.Hermes, id, "@Odin can you check the numbers?");

        // Assert
        await using var db = h.NewDb();
        Assert.Equal(h.Odin.Id, Assert.Single(db.Outbox).TargetActorId);
    }

    [Fact]
    public async Task AddComment_AgentsMentionEachOtherPastTheLimit_IsRefused()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "Plan the launch", BoardStatus.Waiting, "Hermes");
        for (var i = 0; i < h.Board.MaxAgentMentionHops; i++)
            await CommentAsync(h, i % 2 == 0 ? h.Hermes : h.Odin, id, i % 2 == 0 ? "@Odin?" : "@Hermes!");

        // Act
        var act = () => CommentAsync(h, h.Odin, id, "@Hermes once more");

        // Assert
        var ex = await Assert.ThrowsAsync<ForbiddenException>(act);
        Assert.Contains("ask_user", ex.Message);
    }

    [Fact]
    public async Task AddComment_AgentPastTheLimitWithoutMention_IsPosted()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "Plan the launch", BoardStatus.Waiting, "Hermes");
        for (var i = 0; i < h.Board.MaxAgentMentionHops; i++) await CommentAsync(h, h.Hermes, id, "@Odin?");

        // Act
        var c = await CommentAsync(h, h.Odin, id, "Here is my answer.");

        // Assert
        Assert.Equal("Here is my answer.", c.Body);
    }

    [Fact]
    public async Task AddComment_UserCommentsAfterTheLimit_LetsAgentsMentionAgain()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "Plan the launch", BoardStatus.Waiting, "Hermes");
        for (var i = 0; i < h.Board.MaxAgentMentionHops; i++) await CommentAsync(h, h.Hermes, id, "@Odin?");
        await CommentAsync(h, h.User, id, "Keep going.");

        // Act
        var c = await CommentAsync(h, h.Hermes, id, "@Odin one more question");

        // Assert
        Assert.Equal(["Odin"], c.Mentions!.Select(m => m.Name));
    }

    [Fact]
    public async Task AddComment_AgentMentionsUser_NotifiesTheUser()
    {
        // Arrange
        using var h = new Harness();
        var id = await h.CreateAsync(h.User, "Plan the launch", BoardStatus.Waiting, "Hermes");

        // Act
        await CommentAsync(h, h.Hermes, id, $"@{h.User.Name} which date works for you?");

        // Assert
        await using var db = h.NewDb();
        var row = Assert.Single(db.Outbox.Where(o => o.Kind == OutboxKind.Notify));
        Assert.Equal("Hermes mentioned you: Plan the launch", row.PayloadJson);
    }
}

/// <summary>Responsibilities: what each agent does, which the dispatcher reads.</summary>
public class ResponsibilitiesTests
{
    [Fact]
    public async Task AskForResponsibilities_ActiveAgent_GivesItATaskInWaiting()
    {
        // Arrange
        using var h = new Harness();

        // Act
        var r = await h.AsUser().AskForResponsibilities(h.Odin.Id, h.Ct);

        // Assert
        Assert.Equal((TaskService.ResponsibilitiesTaskTitle, "Odin", BoardStatus.Waiting), (r.Task.Task.Title, r.Task.Task.Assignee, r.Task.Task.Status));
    }

    [Fact]
    public async Task AskForResponsibilities_ByAnAgent_IsForbidden()
    {
        // Arrange
        using var h = new Harness();

        // Act
        var act = () => h.AsHermes().AskForResponsibilities(h.Odin.Id, h.Ct);

        // Assert
        await Assert.ThrowsAsync<ForbiddenException>(act);
    }

    [Fact]
    public async Task SetResponsibilities_Agent_ShowsInTheActorList()
    {
        // Arrange
        using var h = new Harness();

        // Act
        await new AgentAdminService(h.NewDb(), h.Clock).SetResponsibilities(h.Odin.Id, "  Content: newsletters, posts.  ", h.Ct);

        // Assert
        var actors = await h.AsHermes().GetActorsAsync(h.Ct);
        Assert.Equal("Content: newsletters, posts.", actors.Single(a => a.Name == "Odin").Responsibilities);
    }

    [Fact]
    public async Task SetResponsibilities_TooLong_IsRefused()
    {
        // Arrange
        using var h = new Harness();

        // Act
        var act = () => new AgentAdminService(h.NewDb(), h.Clock).SetResponsibilities(h.Odin.Id, new string('x', AgentAdminService.MaxResponsibilities + 1), h.Ct);

        // Assert
        await Assert.ThrowsAsync<ValidationException>(act);
    }
}
