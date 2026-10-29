using Bunit;
using DoiQ.Client.Pages;
using DoiQ.Shared.Dtos;

namespace DoiQ.Client.Tests;

public sealed class SettingsAgentsTests : ClientTestBase
{
    private async Task<(IRenderedComponent<Settings> Cut, AgentAdminDto Loki)> RenderWithLoki(bool orchestrator = false)
    {
        var loki = (await Api.CreateAgentAsync(new CreateAgentRequest("Loki", IsOrchestrator: orchestrator))).Agent;
        return (Render<Settings>(), loki);
    }

    [Fact]
    public async Task Settings_OrchestratorAgent_ShowsTheOrchestratorBadge()
    {
        // Arrange
        // Act
        var (cut, loki) = await RenderWithLoki(orchestrator: true);

        // Assert
        var card = cut.Find($"#agent-del-{loki.Id}").Closest("article")!;
        Assert.Contains("Orchestrator", card.QuerySelectorAll(".badge-new").Select(b => b.TextContent));
    }

    [Fact]
    public async Task Settings_DeleteAgent_AsksBeforeDeleting()
    {
        // Arrange
        var (cut, loki) = await RenderWithLoki();

        // Act
        cut.Find($"#agent-del-{loki.Id}").Click();

        // Assert
        Assert.Contains("Loki", cut.Find("#confirm-body").TextContent);
        Assert.Contains(await Api.GetAgentsAsync(), a => a.Id == loki.Id);
    }

    [Fact]
    public async Task Settings_ConfirmingTheAgentDelete_RemovesTheAgentAndSaysSo()
    {
        // Arrange
        var (cut, loki) = await RenderWithLoki();
        cut.Find($"#agent-del-{loki.Id}").Click();

        // Act
        cut.Find("#confirm-ok").Click();

        // Assert
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#confirm-dialog")));
        Assert.DoesNotContain(await Api.GetAgentsAsync(), a => a.Id == loki.Id);
        Assert.Empty(cut.FindAll($"#agent-del-{loki.Id}"));
        Assert.Contains(Notices.Items, n => n.Text.Contains("Loki was deleted"));
    }
}
