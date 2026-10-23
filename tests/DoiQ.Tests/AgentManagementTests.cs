using DoiQ.Core.Data;
using DoiQ.Core.Options;
using DoiQ.Core.Services;
using DoiQ.Shared.Dtos;
using DoiQ.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DoiQ.Tests;

public class SetupTests
{
    private static AccountService Account(Harness h) => new(h.NewDb());

    [Fact]
    public async Task GetStatus_BeforeSetup_ReportsNeedsSetup()
    {
        // Arrange
        using var h = new Harness();

        // Act
        var status = await Account(h).GetStatus(h.Ct);

        // Assert
        Assert.True(status.NeedsSetup);
    }

    [Fact]
    public async Task Verify_BeforeSetup_ReturnsNull()
    {
        // Arrange
        using var h = new Harness();

        // Act
        var result = await Account(h).Verify("whatever", h.Ct);

        // Assert
        Assert.Null(result);   // not set up yet
    }

    [Fact]
    public async Task Setup_FirstCall_ClearsNeedsSetupAndStoresOnlyAHash()
    {
        // Arrange
        using var h = new Harness();

        // Act
        await Account(h).Setup("correct horse", h.Ct);

        // Assert
        Assert.False((await Account(h).GetStatus(h.Ct)).NeedsSetup);
        await using var db = h.NewDb();
        Assert.DoesNotContain("correct horse", (await db.Settings.SingleAsync()).Value);
    }

    [Fact]
    public async Task Setup_SecondCall_ThrowsConflict()
    {
        // Arrange
        using var h = new Harness();
        await Account(h).Setup("correct horse", h.Ct);

        // Act
        var ex = await Record.ExceptionAsync(() => Account(h).Setup("another one!", h.Ct));

        // Assert
        Assert.IsType<ConflictException>(ex);
    }

    [Fact]
    public async Task Verify_CorrectPasswordAfterSetup_ReturnsTrue()
    {
        // Arrange
        using var h = new Harness();
        await Account(h).Setup("correct horse", h.Ct);

        // Act
        var result = await Account(h).Verify("correct horse", h.Ct);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task Verify_WrongPasswordAfterSetup_ReturnsFalse()
    {
        // Arrange
        using var h = new Harness();
        await Account(h).Setup("correct horse", h.Ct);

        // Act
        var result = await Account(h).Verify("wrong", h.Ct);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task Setup_ShortPassword_ThrowsValidation()
    {
        // Arrange
        using var h = new Harness();

        // Act
        var ex = await Record.ExceptionAsync(() => Account(h).Setup("short", h.Ct));

        // Assert
        Assert.IsType<ValidationException>(ex);
    }

    [Fact]
    public async Task Change_WrongCurrentPassword_ThrowsValidation()
    {
        // Arrange
        using var h = new Harness();
        await Account(h).Setup("first password", h.Ct);

        // Act
        var ex = await Record.ExceptionAsync(() => Account(h).Change("nope", "second password", h.Ct));

        // Assert
        Assert.IsType<ValidationException>(ex);
    }

    [Fact]
    public async Task Change_CorrectCurrentPassword_ReplacesThePassword()
    {
        // Arrange
        using var h = new Harness();
        await Account(h).Setup("first password", h.Ct);

        // Act
        await Account(h).Change("first password", "second password", h.Ct);

        // Assert
        Assert.True(await Account(h).Verify("second password", h.Ct));
        Assert.False(await Account(h).Verify("first password", h.Ct));
    }
}

public class AgentAdminTests
{
    private static AgentAdminService Admin(Harness h) => new(h.NewDb(), h.Clock);
    private static ActorService Actors(Harness h) => new(h.NewDb(), h.Clock);

    [Fact]
    public async Task CreateAsync_WithoutSecret_ReturnsKeyAndGeneratedSecretOnce()
    {
        // Arrange
        using var h = new Harness();

        // Act
        var r = await Admin(h).CreateAsync(new CreateAgentRequest("Loki", WebhookUrl: "http://loki/hook"));

        // Assert
        Assert.StartsWith("dq_", r.ApiKey);
        Assert.NotNull(r.WebhookSecret);   // generated because none was sent
        Assert.True(r.Agent.HasWebhookSecret);
        Assert.Equal("L", r.Agent.Initials);
    }

    [Fact]
    public async Task CreateAsync_Agent_StoresOnlyTheKeyHashAndTheKeyAuthenticates()
    {
        // Arrange
        using var h = new Harness();

        // Act
        var r = await Admin(h).CreateAsync(new CreateAgentRequest("Loki"));

        // Assert
        var info = await Actors(h).AuthenticateApiKeyAsync(r.ApiKey);
        Assert.Equal("Loki", info!.Name);
        await using var db = h.NewDb();
        var row = await db.Actors.SingleAsync(a => a.Name == "Loki");
        Assert.NotEqual(r.ApiKey, row.ApiKeyHash);
        Assert.Equal(ApiKeyHasher.Hash(r.ApiKey), row.ApiKeyHash);
    }

    [Fact]
    public async Task ListAsync_AgentWithSecret_ReportsHasSecretWithoutCarryingIt()
    {
        // Arrange
        using var h = new Harness();
        await Admin(h).CreateAsync(new CreateAgentRequest("Loki", WebhookUrl: "http://loki/hook"));

        // Act
        var listed = (await Admin(h).ListAsync()).Single(a => a.Name == "Loki");

        // Assert
        Assert.True(listed.HasWebhookSecret);   // the DTO has no secret field at all
    }

    [Fact]
    public async Task CreateAsync_CallerSuppliesSecret_ReturnsNoGeneratedSecret()
    {
        // Arrange
        using var h = new Harness();

        // Act
        var r = await Admin(h).CreateAsync(new CreateAgentRequest("Thor", WebhookSecret: "mine"));

        // Assert
        Assert.Null(r.WebhookSecret);   // nothing generated to show
    }

    [Fact]
    public async Task RotateKeyAsync_Agent_ReturnsADifferentKey()
    {
        // Arrange
        using var h = new Harness();
        var created = await Admin(h).CreateAsync(new CreateAgentRequest("Loki"));

        // Act
        var rotated = await Admin(h).RotateKeyAsync(created.Agent.Id);

        // Assert
        Assert.NotEqual(created.ApiKey, rotated.ApiKey);
        Assert.NotNull(await Actors(h).AuthenticateApiKeyAsync(rotated.ApiKey));
    }

    [Fact]
    public async Task AuthenticateApiKeyAsync_OldKeyAfterRotation_ReturnsNull()
    {
        // Arrange
        using var h = new Harness();
        var created = await Admin(h).CreateAsync(new CreateAgentRequest("Loki"));

        // Assume
        Assert.NotNull(await Actors(h).AuthenticateApiKeyAsync(created.ApiKey));
        await Admin(h).RotateKeyAsync(created.Agent.Id);

        // Act
        var info = await Actors(h).AuthenticateApiKeyAsync(created.ApiKey);

        // Assert
        Assert.Null(info);
    }

    [Fact]
    public async Task AuthenticateApiKeyAsync_DeactivatedAgent_ReturnsNull()
    {
        // Arrange
        using var h = new Harness();
        var loki = await Admin(h).CreateAsync(new CreateAgentRequest("Loki"));
        await Admin(h).SetActiveAsync(loki.Agent.Id, false);

        // Act
        var info = await Actors(h).AuthenticateApiKeyAsync(loki.ApiKey);

        // Assert
        Assert.Null(info);
    }

    [Fact]
    public async Task AuthenticateApiKeyAsync_ReactivatedAgent_ReturnsTheAgent()
    {
        // Arrange
        using var h = new Harness();
        var loki = await Admin(h).CreateAsync(new CreateAgentRequest("Loki"));
        await Admin(h).SetActiveAsync(loki.Agent.Id, false);

        // Act
        await Admin(h).SetActiveAsync(loki.Agent.Id, true);

        // Assert
        Assert.NotNull(await Actors(h).AuthenticateApiKeyAsync(loki.ApiKey));
    }

    [Fact]
    public async Task GetAsync_TaskAssignedToDeactivatedAgent_KeepsTheAssignment()
    {
        // Arrange
        using var h = new Harness();
        var loki = await Admin(h).CreateAsync(new CreateAgentRequest("Loki"));
        var taskId = await h.CreateAsync(h.User, "for loki", BoardStatus.Waiting, "Loki");
        await Admin(h).SetActiveAsync(loki.Agent.Id, false);

        // Act
        var detail = await h.AsUser().GetAsync(taskId);

        // Assert
        Assert.Equal("Loki", detail.Task.Assignee);   // still assigned
    }

    [Fact]
    public async Task AddCommentAsync_OnTaskOfDeactivatedAgent_QueuesNoNewWebhook()
    {
        // Arrange
        using var h = new Harness();
        var loki = await Admin(h).CreateAsync(new CreateAgentRequest("Loki", WebhookUrl: "http://loki/hook"));
        var taskId = await h.CreateAsync(h.User, "for loki", BoardStatus.Waiting, "Loki");
        await Admin(h).SetActiveAsync(loki.Agent.Id, false);

        // Act
        await h.AsUser().AddComment(taskId, new AddCommentRequest("ping"));   // would wake an active Loki

        // Assert
        await using var db = h.NewDb();
        Assert.Equal(1, await db.Outbox.CountAsync(o => o.TargetActorId == loki.Agent.Id));   // only the one queued while active
    }

    [Fact]
    public async Task AssignAsync_PoolTaskWithDeactivatedAgent_QueuesNothingForThatAgent()
    {
        // Arrange
        using var h = new Harness();
        var loki = await Admin(h).CreateAsync(new CreateAgentRequest("Loki", WebhookUrl: "http://loki/hook"));
        await Admin(h).SetActiveAsync(loki.Agent.Id, false);
        var pool = await h.CreateAsync(h.User, "pool", BoardStatus.Waiting);

        // Act
        await h.AsUser().AssignAsync(pool, new AssignRequest("any"));

        // Assert
        await using var db = h.NewDb();
        Assert.DoesNotContain(await db.Outbox.ToListAsync(), o => o.TaskId == pool && o.TargetActorId == loki.Agent.Id);
    }

    [Fact]
    public async Task AssignAsync_ToDeactivatedAgent_ThrowsValidation()
    {
        // Arrange
        using var h = new Harness();
        var loki = await Admin(h).CreateAsync(new CreateAgentRequest("Loki"));
        await Admin(h).SetActiveAsync(loki.Agent.Id, false);
        var pool = await h.CreateAsync(h.User, "pool", BoardStatus.Waiting, "any");

        // Act
        var ex = await Record.ExceptionAsync(() => h.AsUser().AssignAsync(pool, new AssignRequest("Loki")));

        // Assert
        Assert.IsType<ValidationException>(ex);   // cannot be newly assigned
    }

    [Fact]
    public async Task CreateAsync_NameTakenIgnoringCase_ThrowsConflict()
    {
        // Arrange
        using var h = new Harness();   // Hermes is seeded

        // Act
        var ex = await Record.ExceptionAsync(() => Admin(h).CreateAsync(new CreateAgentRequest("hermes")));

        // Assert
        Assert.IsType<ConflictException>(ex);
    }

    [Fact]
    public async Task CreateAsync_NameOfTheHumanActor_ThrowsConflict()
    {
        // Arrange
        using var h = new Harness();

        // Act
        var ex = await Record.ExceptionAsync(() => Admin(h).CreateAsync(new CreateAgentRequest(h.User.Name)));

        // Assert
        Assert.IsType<ConflictException>(ex);
    }

    [Fact]
    public async Task CreateAsync_ReservedPoolName_ThrowsValidation()
    {
        // Arrange
        using var h = new Harness();

        // Act
        var ex = await Record.ExceptionAsync(() => Admin(h).CreateAsync(new CreateAgentRequest("any")));

        // Assert
        Assert.IsType<ValidationException>(ex);
    }

    [Fact]
    public async Task CreateAsync_NewTriageAgent_TakesOverTriageFromTheOldOne()
    {
        // Arrange
        using var h = new Harness();   // Hermes is seeded as triage

        // Act
        await Admin(h).CreateAsync(new CreateAgentRequest("Loki", IsTriage: true));

        // Assert
        var agents = await Admin(h).ListAsync();
        Assert.Equal(["Loki"], agents.Where(a => a.IsTriage).Select(a => a.Name));
    }

    [Fact]
    public async Task UpdateAsync_MakeAgentTriage_LeavesExactlyOneTriageAgent()
    {
        // Arrange
        using var h = new Harness();
        await Admin(h).CreateAsync(new CreateAgentRequest("Loki", IsTriage: true));

        // Act
        await Admin(h).UpdateAsync(h.Odin.Id, new UpdateAgentRequest("Odin", IsTriage: true));

        // Assert
        var agents = await Admin(h).ListAsync();
        Assert.Equal(["Odin"], agents.Where(a => a.IsTriage).Select(a => a.Name));
    }

    [Fact]
    public async Task UpdateAsync_RenameToExistingNameIgnoringCase_ThrowsConflict()
    {
        // Arrange
        using var h = new Harness();
        var loki = await Admin(h).CreateAsync(new CreateAgentRequest("Loki"));

        // Act
        var ex = await Record.ExceptionAsync(() => Admin(h).UpdateAsync(loki.Agent.Id, new UpdateAgentRequest("ODIN")));

        // Assert
        Assert.IsType<ConflictException>(ex);
    }

    [Fact]
    public async Task UpdateAsync_WithoutNewSecret_KeepsTheStoredSecret()
    {
        // Arrange
        using var h = new Harness();
        var c = await Admin(h).CreateAsync(new CreateAgentRequest("Loki", WebhookSecret: "old"));

        // Act
        await Admin(h).UpdateAsync(c.Agent.Id, new UpdateAgentRequest("Loki", WebhookUrl: "http://x"));

        // Assert
        await using var db = h.NewDb();
        Assert.Equal("old", (await db.Actors.SingleAsync(a => a.Name == "Loki")).WebhookSecret);
    }

    [Fact]
    public async Task UpdateAsync_WithNewSecret_ReplacesTheStoredSecret()
    {
        // Arrange
        using var h = new Harness();
        var c = await Admin(h).CreateAsync(new CreateAgentRequest("Loki", WebhookSecret: "old"));

        // Act
        await Admin(h).UpdateAsync(c.Agent.Id, new UpdateAgentRequest("Loki", WebhookSecret: "new"));

        // Assert
        await using var db = h.NewDb();
        Assert.Equal("new", (await db.Actors.SingleAsync(a => a.Name == "Loki")).WebhookSecret);
    }

    [Fact]
    public async Task AuthenticateApiKeyAsync_FirstUse_SetsLastSeenAt()
    {
        // Arrange
        using var h = new Harness();
        var c = await Admin(h).CreateAsync(new CreateAgentRequest("Loki"));

        // Assume
        Assert.Null((await Admin(h).ListAsync()).Single(a => a.Name == "Loki").LastSeenAt);

        // Act
        await Actors(h).AuthenticateApiKeyAsync(c.ApiKey);

        // Assert
        Assert.Equal(h.Clock.GetUtcNow().UtcDateTime, (await Admin(h).ListAsync()).Single(a => a.Name == "Loki").LastSeenAt);
    }

    [Fact]
    public async Task AuthenticateApiKeyAsync_WithinThirtySeconds_KeepsLastSeenAt()
    {
        // Arrange
        using var h = new Harness();
        var c = await Admin(h).CreateAsync(new CreateAgentRequest("Loki"));
        await Actors(h).AuthenticateApiKeyAsync(c.ApiKey);
        var first = (await Admin(h).ListAsync()).Single(a => a.Name == "Loki").LastSeenAt;
        h.Clock.Advance(TimeSpan.FromSeconds(30));

        // Act
        await Actors(h).AuthenticateApiKeyAsync(c.ApiKey);

        // Assert
        Assert.Equal(first, (await Admin(h).ListAsync()).Single(a => a.Name == "Loki").LastSeenAt);
    }

    [Fact]
    public async Task AuthenticateApiKeyAsync_AfterMoreThanAMinute_UpdatesLastSeenAt()
    {
        // Arrange
        using var h = new Harness();
        var c = await Admin(h).CreateAsync(new CreateAgentRequest("Loki"));
        await Actors(h).AuthenticateApiKeyAsync(c.ApiKey);
        var first = (await Admin(h).ListAsync()).Single(a => a.Name == "Loki").LastSeenAt;
        h.Clock.Advance(TimeSpan.FromSeconds(61));

        // Act
        await Actors(h).AuthenticateApiKeyAsync(c.ApiKey);

        // Assert
        Assert.NotEqual(first, (await Admin(h).ListAsync()).Single(a => a.Name == "Loki").LastSeenAt);
    }

    [Fact]
    public async Task ActorService_SeedWithEmptyConfig_KeepsASingleHumanActor()
    {
        // Arrange
        using var h = new Harness();

        // Act
        await Actors(h).Seed(new AuthOptions(), [], h.Ct);

        // Assert
        await using var db = h.NewDb();
        Assert.Equal(1, await db.Actors.CountAsync(a => a.Kind == ActorKind.Human));
    }

    [Fact]
    public async Task ActorService_SeedWithExistingAgent_LeavesKeyAndTriageChoiceAlone()
    {
        // Arrange
        using var h = new Harness();
        var rotated = await Admin(h).RotateKeyAsync(h.Hermes.Id);
        await Admin(h).UpdateAsync(h.Hermes.Id, new UpdateAgentRequest("Hermes", IsTriage: false, WebhookUrl: "https://ui.example/hook"));

        // Act
        await Actors(h).Seed(new AuthOptions(), [new AgentSeed("Hermes", "X", "env-key", IsTriage: true, WebhookUrl: "https://env.example/hook")], h.Ct);

        // Assert
        Assert.NotNull(await Actors(h).AuthenticateApiKeyAsync(rotated.ApiKey));   // the key rotated in Settings survives
        Assert.Null(await Actors(h).AuthenticateApiKeyAsync("env-key"));
        var hermes = (await Admin(h).ListAsync()).Single(a => a.Name == "Hermes");
        Assert.False(hermes.IsTriage);
        Assert.Equal("H", hermes.Initials);
        Assert.Equal("https://ui.example/hook", hermes.WebhookUrl);
    }

    [Fact]
    public async Task ActorService_SeedWithNewAgent_CreatesItWithTheConfiguredKey()
    {
        // Arrange
        using var h = new Harness();

        // Act
        await Actors(h).Seed(new AuthOptions(), [new AgentSeed("Loki", "L", "loki-key", IsTriage: false, NotifyUrl: "https://loki.example/n")], h.Ct);

        // Assert
        Assert.NotNull(await Actors(h).AuthenticateApiKeyAsync("loki-key"));
        Assert.Equal("https://loki.example/n", (await Admin(h).ListAsync()).Single(a => a.Name == "Loki").NotifyUrl);
    }

    [Fact]
    public async Task ActorService_SeedWithTwoTriageAgentsAndNoneBefore_MakesOnlyTheFirstTriage()
    {
        // Arrange
        using var h = new Harness();
        await Admin(h).UpdateAsync(h.Hermes.Id, new UpdateAgentRequest("Hermes", IsTriage: false));

        // Act
        await Actors(h).Seed(new AuthOptions(), [
            new AgentSeed("Loki", "L", "loki-key", IsTriage: true),
            new AgentSeed("Thor", "T", "thor-key", IsTriage: true),
        ], h.Ct);

        // Assert
        var triage = (await Admin(h).ListAsync()).Where(a => a.IsTriage).Select(a => a.Name);
        Assert.Equal(["Loki"], triage);
    }

    [Fact]
    public async Task ActorService_SeedTriageAgentWhenOneExists_CreatesItWithoutTheTriageFlag()
    {
        // Arrange
        using var h = new Harness();   // Hermes is triage

        // Act
        await Actors(h).Seed(new AuthOptions(), [new AgentSeed("Loki", "L", "loki-key", IsTriage: true)], h.Ct);

        // Assert
        var agents = await Admin(h).ListAsync();
        Assert.False(agents.Single(a => a.Name == "Loki").IsTriage);
        Assert.True(agents.Single(a => a.Name == "Hermes").IsTriage);
    }
}
