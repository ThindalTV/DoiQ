using System.Net;
using DoiQ.Core.Domain;
using DoiQ.Core.Outbox;
using DoiQ.Shared.Dtos;
using DoiQ.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DoiQ.Tests;

public class OutboxProcessorTests
{
    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Req, string Body)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request, await request.Content!.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(status);
        }
    }

    private static async Task ConfigureHermesAsync(Harness h)
    {
        await using var db = h.NewDb();
        var a = await db.Actors.SingleAsync(x => x.Name == "Hermes");
        a.WebhookUrl = "http://hermes/webhooks/todo";
        a.WebhookSecret = "s3cret";
        a.NotifyUrl = "http://hermes/webhooks/todo-notify";
        await db.SaveChangesAsync();
    }

    private static OutboxProcessor Processor(Harness h, StubHandler handler, OutboxOptions? options = null) =>
        new(h.NewDb(), new HttpClient(handler), h.Clock, Options.Create(options ?? new OutboxOptions()), NullLogger<OutboxProcessor>.Instance);

    [Fact]
    public async Task ProcessDueAsync_InsideTheDebounceWindow_SendsNothing()
    {
        // Arrange
        using var h = new Harness();
        await ConfigureHermesAsync(h);
        await h.CreateAsync(h.User, "wake hermes");
        var handler = new StubHandler(HttpStatusCode.OK);

        // Act
        var sent = await Processor(h, handler).ProcessDueAsync();   // inside the 30 s window

        // Assert
        Assert.Equal(0, sent);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task ProcessDueAsync_AfterTheDebounce_PostsAV2SignedPayload()
    {
        // Arrange
        using var h = new Harness();
        await ConfigureHermesAsync(h);
        await h.CreateAsync(h.User, "wake hermes");
        var handler = new StubHandler(HttpStatusCode.OK);
        h.Clock.Advance(TimeSpan.FromSeconds(31));

        // Act
        var sent = await Processor(h, handler).ProcessDueAsync();

        // Assert
        Assert.Equal(1, sent);
        var (req, body) = Assert.Single(handler.Calls);
        var ts = req.Headers.GetValues("X-Webhook-Timestamp").Single();
        Assert.Equal(OutboxProcessor.Sign("s3cret", ts, body), req.Headers.GetValues("X-Webhook-Signature-V2").Single());
        Assert.Contains("\"title\":\"wake hermes\"", body);
        Assert.Contains("\"actor\":\"User\"", body);
    }

    [Fact]
    public async Task ProcessDueAsync_AfterASuccessfulSend_MarksTheRowSent()
    {
        // Arrange
        using var h = new Harness();
        await ConfigureHermesAsync(h);
        await h.CreateAsync(h.User, "wake hermes");
        h.Clock.Advance(TimeSpan.FromSeconds(31));

        // Act
        await Processor(h, new StubHandler(HttpStatusCode.OK)).ProcessDueAsync();

        // Assert
        await using var db = h.NewDb();
        Assert.NotNull((await db.Outbox.SingleAsync()).SentAt);
    }

    [Fact]
    public async Task ProcessDueAsync_CalledAgainAfterSending_DoesNotSendTwice()
    {
        // Arrange
        using var h = new Harness();
        await ConfigureHermesAsync(h);
        await h.CreateAsync(h.User, "wake hermes");
        var handler = new StubHandler(HttpStatusCode.OK);
        h.Clock.Advance(TimeSpan.FromSeconds(31));
        await Processor(h, handler).ProcessDueAsync();

        // Act
        var sent = await Processor(h, handler).ProcessDueAsync();

        // Assert
        Assert.Equal(0, sent);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task ProcessDueAsync_EndpointFails_SchedulesARetryWithBackoff()
    {
        // Arrange
        using var h = new Harness();
        await ConfigureHermesAsync(h);
        await h.CreateAsync(h.User, "x");
        h.Clock.Advance(TimeSpan.FromSeconds(31));

        // Act
        var sent = await Processor(h, new StubHandler(HttpStatusCode.InternalServerError)).ProcessDueAsync();

        // Assert
        Assert.Equal(0, sent);
        await using var db = h.NewDb();
        var row = await db.Outbox.SingleAsync();
        Assert.Equal(1, row.Attempts);
        Assert.Null(row.SentAt);
        Assert.True(row.NotBefore > h.Clock.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task ProcessDueAsync_EndpointKeepsFailing_GivesUpAfterMaxAttemptsKeepingTheError()
    {
        // Arrange
        using var h = new Harness();
        await ConfigureHermesAsync(h);
        await h.CreateAsync(h.User, "x");
        var handler = new StubHandler(HttpStatusCode.InternalServerError);
        h.Clock.Advance(TimeSpan.FromSeconds(31));
        await Processor(h, handler).ProcessDueAsync();

        // Act
        for (var i = 0; i < new OutboxOptions().MaxAttempts; i++)
        {
            h.Clock.Advance(TimeSpan.FromHours(2));
            await Processor(h, handler).ProcessDueAsync();
        }

        // Assert
        await using var db = h.NewDb();
        var final = await db.Outbox.SingleAsync();
        Assert.NotNull(final.SentAt);   // gave up, error kept
        Assert.NotNull(final.LastError);
    }

    [Fact]
    public async Task ProcessDueAsync_EndpointFailsWithShortConfiguredDelay_RetriesAfterThatDelay()
    {
        // Arrange
        using var h = new Harness();
        await ConfigureHermesAsync(h);
        await h.CreateAsync(h.User, "x");
        h.Clock.Advance(TimeSpan.FromSeconds(31));
        var options = new OutboxOptions { RetryBaseSeconds = 2 };
        await Processor(h, new StubHandler(HttpStatusCode.InternalServerError), options).ProcessDueAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        var handler = new StubHandler(HttpStatusCode.OK);

        // Act
        var sent = await Processor(h, handler, options).ProcessDueAsync();

        // Assert
        Assert.Equal(1, sent);
    }

    [Fact]
    public async Task ProcessDueAsync_AgentDeactivatedWhileQueued_SendsNothingAndClosesTheRow()
    {
        // Arrange
        using var h = new Harness();
        await ConfigureHermesAsync(h);
        await h.CreateAsync(h.User, "x");
        await using (var db = h.NewDb())
        {
            (await db.Actors.SingleAsync(a => a.Name == "Hermes")).IsActive = false;
            await db.SaveChangesAsync();
        }
        h.Clock.Advance(TimeSpan.FromSeconds(31));
        var handler = new StubHandler(HttpStatusCode.OK);

        // Act
        var sent = await Processor(h, handler).ProcessDueAsync();

        // Assert
        Assert.Equal(0, sent);
        Assert.Empty(handler.Calls);
        await using var check = h.NewDb();
        Assert.NotNull((await check.Outbox.SingleAsync()).SentAt);
    }

    [Fact]
    public async Task CreateAsync_NewTaskByTheUser_QueuesOnlyForTheTriageAgent()
    {
        // Arrange
        using var h = new Harness();

        // Act
        await h.CreateAsync(h.User, "untriaged");

        // Assert
        await using var db = h.NewDb();
        var row = await db.Outbox.SingleAsync();
        Assert.Equal(h.Hermes.Id, row.TargetActorId);
    }

    [Fact]
    public async Task CreateAsync_PoolTaskByTheUser_QueuesForEveryActiveAgentButNotInactiveOnes()
    {
        // Arrange
        using var h = new Harness();
        await using (var db = h.NewDb())
        {
            (await db.Actors.SingleAsync(a => a.Name == "Odin")).IsActive = false;
            await db.SaveChangesAsync();
        }

        // Act
        await h.CreateAsync(h.User, "pool", BoardStatus.Waiting, "any");

        // Assert
        await using var check = h.NewDb();
        var row = await check.Outbox.SingleAsync();
        Assert.Equal(h.Hermes.Id, row.TargetActorId);
    }

    [Fact]
    public async Task CreateAsync_TaskByAnAgentForAnotherAgent_WakesThatAgentOnly()
    {
        // Arrange
        using var h = new Harness();

        // Act
        await h.CreateAsync(h.Hermes, "delegated", BoardStatus.Waiting, "Odin");

        // Assert
        await using var db = h.NewDb();
        var row = Assert.Single(db.Outbox);
        Assert.Equal((OutboxKind.Todo, h.Odin.Id), (row.Kind, row.TargetActorId));
    }

    [Fact]
    public async Task CreateAsync_TaskByAnAgentForItself_QueuesNothing()
    {
        // Arrange
        using var h = new Harness();

        // Act
        await h.CreateAsync(h.Hermes, "mine", BoardStatus.Waiting, "Hermes");

        // Assert
        await using var db = h.NewDb();
        Assert.Empty(db.Outbox);
    }

    [Fact]
    public async Task ProcessDueAsync_NotifyRow_PostsToTheAgentsNotifyUrl()
    {
        // Arrange
        using var h = new Harness();
        await ConfigureHermesAsync(h);
        await h.CreateAsync(h.Hermes, "for user", BoardStatus.Waiting, "User");
        var handler = new StubHandler(HttpStatusCode.OK);
        h.Clock.Advance(TimeSpan.FromSeconds(31));

        // Act
        await Processor(h, handler).ProcessDueAsync();

        // Assert
        var (req, body) = Assert.Single(handler.Calls);
        Assert.Equal("http://hermes/webhooks/todo-notify", req.RequestUri!.ToString());
        Assert.Contains("Hermes assigned you: for user", body);
    }
}
