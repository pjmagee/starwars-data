using StarWarsData.Services;
using StarWarsData.Tests.Infrastructure;

namespace StarWarsData.Tests.Agent;

/// <summary>
/// Agent-tier routing tests for the <c>render_family_tree</c> tool added in
/// feature 042. Verifies the FAMILY-TREE ROUTING block in <see cref="AskAIAgent"/>'s
/// system prompt steers kinship phrasing to <c>render_family_tree</c> and keeps
/// hierarchy/command-chain phrasing on <c>render_graph</c> (the anti-pattern
/// boundary spelled out in specs/042-family-tree-component/contracts/render-family-tree-tool.md).
///
/// Live OpenAI + <c>starwars-dev</c> MongoDB. Manual-only tier per Principle III.
/// </summary>
[TestClass]
[TestCategory(TestTiers.Agent)]
[DoNotParallelize]
public class FamilyTreeAgentRoutingTests
{
    [ClassInitialize]
    public static async Task ClassSetup(TestContext _) => await AgentFixture.EnsureInitializedAsync();

    [TestMethod]
    public async Task SkywalkerFamilyTree_RoutesToRenderFamilyTree()
    {
        var capture = new ConversationCapture();
        await AgentFixture.RunPrompt("Show me the Skywalker family tree centered on Anakin Skywalker", capture);

        var toolNames = string.Join(" → ", capture.ToolCalls.Select(t => t.Name));
        Console.WriteLine($"[DIAG] SkywalkerFamilyTree tool chain: {toolNames}");

        Assert.IsTrue(capture.HasToolCall(ToolNames.Component.RenderFamilyTree), $"Kinship prompt should route to render_family_tree. Actual calls: {toolNames}");
        Assert.IsFalse(capture.HasToolCall(ToolNames.Component.RenderGraph), $"Kinship prompt should NOT call render_graph. Actual calls: {toolNames}");
    }

    [TestMethod]
    public async Task AnakinLineage_RoutesToRenderFamilyTree()
    {
        var capture = new ConversationCapture();
        await AgentFixture.RunPrompt("What is Anakin Skywalker's lineage?", capture);

        var toolNames = string.Join(" → ", capture.ToolCalls.Select(t => t.Name));
        Console.WriteLine($"[DIAG] AnakinLineage tool chain: {toolNames}");

        Assert.IsTrue(capture.HasToolCall(ToolNames.Component.RenderFamilyTree), $"Alternate kinship phrasing ('lineage') should route to render_family_tree. Actual calls: {toolNames}");
        Assert.IsFalse(capture.HasToolCall(ToolNames.Component.RenderGraph), $"Alternate kinship phrasing should NOT call render_graph. Actual calls: {toolNames}");
    }

    [TestMethod]
    public async Task SithOrderCommandStructure_RoutesToRenderGraphNotFamilyTree()
    {
        var capture = new ConversationCapture();
        await AgentFixture.RunPrompt("Show me the Sith Order command structure", capture);

        var toolNames = string.Join(" → ", capture.ToolCalls.Select(t => t.Name));
        Console.WriteLine($"[DIAG] SithOrderCommandStructure tool chain: {toolNames}");

        Assert.IsTrue(capture.HasToolCall(ToolNames.Component.RenderGraph), $"Hierarchy/command-structure prompt should route to render_graph (Tree mode). Actual calls: {toolNames}");
        Assert.IsFalse(
            capture.HasToolCall(ToolNames.Component.RenderFamilyTree),
            $"Hierarchy/command-structure prompt MUST NOT route to render_family_tree (kinship-vs-hierarchy boundary). Actual calls: {toolNames}"
        );
    }
}
