using StarWarsData.Services;
using StarWarsData.Tests.Infrastructure;

namespace StarWarsData.Tests.Agent;

[TestClass]
[TestCategory(TestTiers.Agent)]
[DoNotParallelize]
public class RelationshipQueryTests
{
    [ClassInitialize]
    public static async Task ClassSetup(TestContext _) => await AgentFixture.EnsureInitializedAsync();

    private static EvaluatorAgent Evaluator => new(AgentFixture.EvaluatorClient);

    // FamilyTree_UsesKGLabelsAndTreeLayout was removed in commit <042 Phase 5> —
    // it asserted "Family tree of Anakin Skywalker" routes to render_graph
    // (layoutMode=tree). Design-042 introduced the dedicated render_family_tree
    // tool and the FAMILY-TREE ROUTING prompt block in AskAIAgent so kinship
    // phrasing now routes to render_family_tree (NOT render_graph). The new
    // FamilyTreeAgentRoutingTests in the same Agent/ folder cover the
    // replacement assertion. Keeping this comment so future readers don't
    // re-add a test that reasserts the old (now-incorrect) routing.

    [TestMethod]
    public async Task PoliticalHierarchy_UsesGovernmentLabels()
    {
        var capture = new ConversationCapture();
        await AgentFixture.RunPrompt("Show the political hierarchy of the Galactic Empire", capture);

        Assert.IsTrue(capture.HasToolCall(ToolNames.GraphRAG.SearchEntities), "Should search for Galactic Empire");

        Assert.IsTrue(
            capture.HasToolCall(ToolNames.Component.RenderGraph)
                || capture.HasToolCall(ToolNames.GraphRAG.GetEntityRelationships)
                || capture.HasToolCall(ToolNames.GraphRAG.GetLineage)
                || capture.HasToolCall(ToolNames.GraphRAG.TraverseGraph)
                || capture.HasToolCall(ToolNames.GraphRAG.SemanticSearch),
            "Should visualize or fetch relationships for the hierarchy"
        );

        Assert.IsTrue(capture.HasToolCallStartingWith("render_"), "Should call a render tool to present results");

        var eval = await Evaluator.EvaluateAsync(
            "Show the political hierarchy of the Galactic Empire",
            capture.ToolCalls,
            capture.FinalResponse,
            "Should: 1) search_entities for Galactic Empire, "
                + "2) get_relationship_types to discover labels and/or get_entity_relationships, "
                + "3) render_graph with hierarchy labels OR render_markdown/render_data_table with relationship data. "
                + "Either approach is acceptable."
        );

        Assert.IsTrue(eval.Score >= 3, $"Evaluator score {eval.Score}/5: {eval.Reasoning}");
    }

    [TestMethod]
    public async Task HowAreEntitiesConnected_UsesFindConnections()
    {
        var capture = new ConversationCapture();
        await AgentFixture.RunPrompt("How is Emperor Palpatine connected to Luke Skywalker?", capture);

        Assert.IsTrue(capture.HasToolCall(ToolNames.GraphRAG.SearchEntities), "Should search for both entities");
        Assert.IsTrue(capture.HasToolCall(ToolNames.GraphRAG.FindConnections), "Should use find_connections to find the path");

        Assert.IsTrue(capture.HasToolCallStartingWith("render_") || capture.FinalResponse is not null, "Should render the connection path or provide a text response");

        var eval = await Evaluator.EvaluateAsync(
            "How is Emperor Palpatine connected to Luke Skywalker?",
            capture.ToolCalls,
            capture.FinalResponse,
            "Should: 1) search_entities for both Emperor Palpatine (NOT Cosinga Palpatine) and Luke Skywalker, "
                + "2) find_connections with both PageIds, 3) render_markdown or render_data_table "
                + "showing the path between them. Multiple search_entities calls are acceptable "
                + "if needed to resolve both entities. Multiple find_connections attempts are acceptable "
                + "if the first attempt used wrong IDs (e.g. Legends vs Canon variant)."
        );

        Assert.IsTrue(eval.Score >= 2, $"Evaluator score {eval.Score}/5: {eval.Reasoning}");
    }
}
