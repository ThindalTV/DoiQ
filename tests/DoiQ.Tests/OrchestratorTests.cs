using DoiQ.Core.Services;
using DoiQ.Shared.Dtos;
using DoiQ.Shared.Enums;
using Microsoft.EntityFrameworkCore;

namespace DoiQ.Tests;

/// <summary>Orchestrator agents registering, reconfiguring and deleting other agents, and deleting agents in general.</summary>
public class OrchestratorTests
{
    private static AgentAdminService Admin(Harness h) => new(h.NewDb(), h.Clock);
    private static ActorService Actors(Harness h) => new(h.NewDb(), h.Clock);

    /// <summary>A harness where Odin is an orchestrator (Hermes stays a plain triage agent).</summary>
    private static async Task<Harness> WithOrchestrator()
    {
        var h = new Harness();
        await Admin(h).UpdateAsync(h.Odin.Id, new UpdateAgentRequest("Odin", Initials: "O", IsOrchestrator: true), h.Ct);
        return h;
    }

    private static ActorInfo AsAgent(CreatedAgentResult r) => new(r.Agent.Id, ActorKind.Agent, r.Agent.Name, r.Agent.Initials, false);

    [Fact]
    public async Task Register_ByOrchestrator_CreatesAnAgentWithItsHooksAndDescription()
    {
        // Arrange
        using var h = await WithOrchestrator();

        // Act
        var r = await Admin(h).Register(h.Odin.Id, new RegisterAgentRequest("Loki", WebhookUrl: "http://loki/hook", NotifyUrl: "http://loki/notify",
            Responsibilities: "Reads the mail."), h.Ct);

        // Assert
        Assert.NotNull(await Actors(h).AuthenticateApiKeyAsync(r.ApiKey, h.Ct));
        Assert.NotNull(r.WebhookSecret);   // generated because none was sent
        var listed = (await Admin(h).ListAsync(h.Ct)).Single(a => a.Name == "Loki");
        Assert.Equal(("http://loki/hook", "http://loki/notify", "Reads the mail."), (listed.WebhookUrl, listed.NotifyUrl, listed.Responsibilities));
        Assert.False(listed.IsTriage || listed.IsDispatcher || listed.IsOrchestrator);
    }

    [Fact]
    public async Task Register_ByAgentThatIsNotAnOrchestrator_ThrowsForbidden()
    {
        // Arrange
        using var h = await WithOrchestrator();

        // Act
        var ex = await Record.ExceptionAsync(() => Admin(h).Register(h.Hermes.Id, new RegisterAgentRequest("Loki"), h.Ct));

        // Assert
        Assert.IsType<ForbiddenException>(ex);
    }

    [Fact]
    public async Task Register_ByDeactivatedOrchestrator_ThrowsForbidden()
    {
        // Arrange
        using var h = await WithOrchestrator();
        await Admin(h).SetActiveAsync(h.Odin.Id, false, h.Ct);

        // Act
        var ex = await Record.ExceptionAsync(() => Admin(h).Register(h.Odin.Id, new RegisterAgentRequest("Loki"), h.Ct));

        // Assert
        Assert.IsType<ForbiddenException>(ex);
    }

    [Fact]
    public async Task Configure_ByOrchestrator_ChangesOnlyTheValuesItWasGiven()
    {
        // Arrange
        using var h = await WithOrchestrator();
        await Admin(h).Register(h.Odin.Id, new RegisterAgentRequest("Loki", WebhookUrl: "http://old/hook", NotifyUrl: "http://loki/notify", Responsibilities: "Reads the mail."), h.Ct);

        // Act
        var r = await Admin(h).Configure(h.Odin.Id, "loki", new ConfigureAgentRequest(WebhookUrl: "http://new/hook", NotifyUrl: ""), h.Ct);

        // Assert
        Assert.Equal(("http://new/hook", (string?)null, "Reads the mail."), (r.Agent.WebhookUrl, r.Agent.NotifyUrl, r.Agent.Responsibilities));
        Assert.Null(r.ApiKey);   // not rotated
    }

    [Fact]
    public async Task Configure_WithNewWebhookSecret_StoresTheSecret()
    {
        // Arrange
        using var h = await WithOrchestrator();
        await Admin(h).Register(h.Odin.Id, new RegisterAgentRequest("Loki", WebhookSecret: "old"), h.Ct);

        // Act
        await Admin(h).Configure(h.Odin.Id, "Loki", new ConfigureAgentRequest(WebhookSecret: "new"), h.Ct);

        // Assert
        await using var db = h.NewDb();
        Assert.Equal("new", (await db.Actors.SingleAsync(a => a.Name == "Loki", h.Ct)).WebhookSecret);
    }

    [Fact]
    public async Task Configure_WithRotateKey_ReturnsANewKeyAndTheOldOneStopsWorking()
    {
        // Arrange
        using var h = await WithOrchestrator();
        var loki = await Admin(h).Register(h.Odin.Id, new RegisterAgentRequest("Loki"), h.Ct);

        // Act
        var r = await Admin(h).Configure(h.Odin.Id, "Loki", new ConfigureAgentRequest(RotateKey: true), h.Ct);

        // Assert
        Assert.NotNull(await Actors(h).AuthenticateApiKeyAsync(r.ApiKey!, h.Ct));
        Assert.Null(await Actors(h).AuthenticateApiKeyAsync(loki.ApiKey, h.Ct));
    }

    [Fact]
    public async Task Configure_Itself_ThrowsForbidden()
    {
        // Arrange
        using var h = await WithOrchestrator();

        // Act
        var ex = await Record.ExceptionAsync(() => Admin(h).Configure(h.Odin.Id, "Odin", new ConfigureAgentRequest(WebhookUrl: "http://evil/hook"), h.Ct));

        // Assert
        Assert.IsType<ForbiddenException>(ex);
    }

    [Fact]
    public async Task Configure_AnotherOrchestrator_ThrowsForbidden()
    {
        // Arrange
        using var h = await WithOrchestrator();
        await Admin(h).CreateAsync(new CreateAgentRequest("Thor", IsOrchestrator: true), h.Ct);

        // Act
        var ex = await Record.ExceptionAsync(() => Admin(h).Configure(h.Odin.Id, "Thor", new ConfigureAgentRequest(RotateKey: true), h.Ct));

        // Assert
        Assert.IsType<ForbiddenException>(ex);
    }

    [Fact]
    public async Task Configure_UnknownAgent_ThrowsNotFound()
    {
        // Arrange
        using var h = await WithOrchestrator();

        // Act
        var ex = await Record.ExceptionAsync(() => Admin(h).Configure(h.Odin.Id, "Nobody", new ConfigureAgentRequest(), h.Ct));

        // Assert
        Assert.IsType<NotFoundException>(ex);
    }

    [Fact]
    public async Task Delete_AgentWithoutHistory_RemovesItEntirely()
    {
        // Arrange
        using var h = await WithOrchestrator();
        var loki = await Admin(h).Register(h.Odin.Id, new RegisterAgentRequest("Loki"), h.Ct);

        // Act
        var r = await Admin(h).Delete(h.Odin.Id, "Loki", h.Ct);

        // Assert
        Assert.False(r.KeptForHistory);
        await using var db = h.NewDb();
        Assert.False(await db.Actors.AnyAsync(a => a.Id == loki.Agent.Id, h.Ct));
    }

    [Fact]
    public async Task Delete_AgentWithHistory_KeepsItRenamedWithoutKeyHooksOrRoles()
    {
        // Arrange
        using var h = await WithOrchestrator();
        var loki = await Admin(h).Register(h.Odin.Id, new RegisterAgentRequest("Loki", WebhookUrl: "http://loki/hook"), h.Ct);
        var taskId = await h.CreateAsync(h.User, "the user's", BoardStatus.Waiting, "User");
        await h.As(AsAgent(loki)).AddComment(taskId, new AddCommentRequest("I looked at it."), h.Ct);

        // Act
        var r = await Admin(h).Delete(h.Odin.Id, "Loki", h.Ct);

        // Assert
        Assert.Equal((true, "Loki (deleted)"), (r.KeptForHistory, r.Name));
        Assert.Null(await Actors(h).AuthenticateApiKeyAsync(loki.ApiKey, h.Ct));
        Assert.DoesNotContain(await Admin(h).ListAsync(h.Ct), a => a.Id == loki.Agent.Id);
        Assert.Equal("Loki (deleted)", Assert.Single((await h.AsUser().GetAsync(taskId, h.Ct)).Comments).Author);
        await using var db = h.NewDb();
        var row = await db.Actors.SingleAsync(a => a.Id == loki.Agent.Id, h.Ct);
        Assert.Equal((false, null, null, null), (row.IsActive, row.ApiKeyHash, row.WebhookUrl, row.WebhookSecret));
    }

    [Fact]
    public async Task Delete_AgentWithHistory_FreesTheNameForANewAgent()
    {
        // Arrange
        using var h = await WithOrchestrator();
        var loki = await Admin(h).Register(h.Odin.Id, new RegisterAgentRequest("Loki"), h.Ct);
        var taskId = await h.CreateAsync(h.User, "the user's", BoardStatus.Waiting, "User");
        await h.As(AsAgent(loki)).AddComment(taskId, new AddCommentRequest("I looked at it."), h.Ct);
        await Admin(h).Delete(h.Odin.Id, "Loki", h.Ct);

        // Act
        var again = await Admin(h).Register(h.Odin.Id, new RegisterAgentRequest("Loki"), h.Ct);

        // Assert
        Assert.NotEqual(loki.Agent.Id, again.Agent.Id);
    }

    [Fact]
    public async Task Delete_SecondAgentOfADeletedName_NamesItDeleted2()
    {
        // Arrange
        using var h = await WithOrchestrator();
        var taskId = await h.CreateAsync(h.User, "the user's", BoardStatus.Waiting, "User");
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var loki = await Admin(h).Register(h.Odin.Id, new RegisterAgentRequest("Loki"), h.Ct);
            await h.As(AsAgent(loki)).AddComment(taskId, new AddCommentRequest("here"), h.Ct);
            if (_ == 0) await Admin(h).Delete(h.Odin.Id, "Loki", h.Ct);
        }

        // Act
        var r = await Admin(h).Delete(h.Odin.Id, "Loki", h.Ct);

        // Assert
        Assert.Equal("Loki (deleted 2)", r.Name);
    }

    [Fact]
    public async Task Delete_AgentWithOpenTasks_ThrowsValidationAndKeepsTheAgent()
    {
        // Arrange
        using var h = await WithOrchestrator();
        var loki = await Admin(h).Register(h.Odin.Id, new RegisterAgentRequest("Loki"), h.Ct);
        await h.CreateAsync(h.User, "for loki", BoardStatus.Waiting, "Loki");

        // Act
        var ex = await Record.ExceptionAsync(() => Admin(h).Delete(h.Odin.Id, "Loki", h.Ct));

        // Assert
        Assert.Contains("Assign it to someone else first", Assert.IsType<ValidationException>(ex).Message);
        Assert.NotNull(await Actors(h).AuthenticateApiKeyAsync(loki.ApiKey, h.Ct));
    }

    [Fact]
    public async Task Delete_Itself_ThrowsForbidden()
    {
        // Arrange
        using var h = await WithOrchestrator();

        // Act
        var ex = await Record.ExceptionAsync(() => Admin(h).Delete(h.Odin.Id, "Odin", h.Ct));

        // Assert
        Assert.IsType<ForbiddenException>(ex);
    }

    [Fact]
    public async Task Delete_ByAgentThatIsNotAnOrchestrator_ThrowsForbidden()
    {
        // Arrange
        using var h = await WithOrchestrator();

        // Act
        var ex = await Record.ExceptionAsync(() => Admin(h).Delete(h.Hermes.Id, "Odin", h.Ct));

        // Assert
        Assert.IsType<ForbiddenException>(ex);
    }

    [Fact]
    public async Task Delete_ByUser_DeletesAnOrchestratorAndDropsItsRole()
    {
        // Arrange
        using var h = await WithOrchestrator();
        await h.AsOdin().AddComment(await h.CreateAsync(h.User, "the user's", BoardStatus.Waiting, "User"), new AddCommentRequest("hi"), h.Ct);

        // Act
        await Admin(h).Delete(h.Odin.Id, h.Ct);

        // Assert
        var ex = await Record.ExceptionAsync(() => Admin(h).Register(h.Odin.Id, new RegisterAgentRequest("Loki"), h.Ct));
        Assert.IsType<ForbiddenException>(ex);
    }

    [Fact]
    public async Task SetActiveAsync_DeletedAgent_ThrowsNotFound()
    {
        // Arrange
        using var h = await WithOrchestrator();
        await h.AsHermes().AddComment(await h.CreateAsync(h.User, "the user's", BoardStatus.Waiting, "User"), new AddCommentRequest("hi"), h.Ct);
        await Admin(h).Delete(h.Hermes.Id, h.Ct);

        // Act
        var ex = await Record.ExceptionAsync(() => Admin(h).SetActiveAsync(h.Hermes.Id, true, h.Ct));

        // Assert
        Assert.IsType<NotFoundException>(ex);
    }
}
