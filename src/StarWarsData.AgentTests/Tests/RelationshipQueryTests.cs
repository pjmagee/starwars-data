using StarWarsData.AgentTests.Evaluation;
using StarWarsData.AgentTests.Infrastructure;

namespace StarWarsData.AgentTests.Tests;

[TestClass]
public class RelationshipQueryTests
{
    static EvaluatorAgent Evaluator => new(AgentFixture.EvaluatorClient);

    [TestMethod]
    public async Task FamilyTree_UsesKGLabelsAndTreeLayout()
    {
        var capture = new ConversationCapture();
        await AgentFixture.RunPrompt("Family tree of Anakin Skywalker", capture);

        Assert.IsTrue(capture.HasToolCall("search_entities"), "Should search for Anakin Skywalker");

        Assert.IsTrue(
            capture.HasToolCall("get_relationship_types"),
            "Should discover available KG edge labels"
        );

        Assert.IsTrue(capture.HasToolCall("render_graph"), "Should render a graph");

        // Should use tree layout
        Assert.IsTrue(
            capture.HasToolCallWithArg("render_graph", "tree"),
            "Should use layoutMode=tree for family tree"
        );

        var eval = await Evaluator.EvaluateAsync(
            "Family tree of Anakin Skywalker",
            capture.ToolCalls,
            capture.FinalResponse,
            "Should: 1) search_entities for Anakin Skywalker (Character, not Family), "
                + "2) get_relationship_types to discover KG edge labels, "
                + "3) render_graph with family-related labels (child_of, parent_of, partner_of, sibling_of) "
                + "and layoutMode=tree. Root must be a Character entity."
        );

        Assert.IsGreaterThanOrEqualTo(
            3,
            eval.Score,
            $"Evaluator score {eval.Score}/5: {eval.Reasoning}"
        );
    }

    [TestMethod]
    public async Task PoliticalHierarchy_UsesGovernmentLabels()
    {
        var capture = new ConversationCapture();
        await AgentFixture.RunPrompt(
            "Show the political hierarchy of the Galactic Empire",
            capture
        );

        Assert.IsTrue(capture.HasToolCall("search_entities"), "Should search for Galactic Empire");

        // Should use render_graph or render_markdown with relationship data
        Assert.IsTrue(
            capture.HasToolCall("render_graph") || capture.HasToolCall("get_entity_relationships"),
            "Should visualize or fetch relationships for the hierarchy"
        );

        Assert.IsTrue(
            capture.HasToolCallStartingWith("render_"),
            "Should call a render tool to present results"
        );

        var eval = await Evaluator.EvaluateAsync(
            "Show the political hierarchy of the Galactic Empire",
            capture.ToolCalls,
            capture.FinalResponse,
            "Should: 1) search_entities for Galactic Empire, "
                + "2) get_relationship_types to discover labels and/or get_entity_relationships, "
                + "3) render_graph with hierarchy labels OR render_markdown/render_data_table with relationship data. "
                + "Either approach is acceptable."
        );

        Assert.IsGreaterThanOrEqualTo(
            3,
            eval.Score,
            $"Evaluator score {eval.Score}/5: {eval.Reasoning}"
        );
    }

    [TestMethod]
    public async Task HowAreEntitiesConnected_UsesFindConnections()
    {
        var capture = new ConversationCapture();
        await AgentFixture.RunPrompt("How is Palpatine connected to Luke Skywalker?", capture);

        Assert.IsTrue(capture.HasToolCall("search_entities"), "Should search for both entities");

        Assert.IsTrue(
            capture.HasToolCall("find_connections"),
            "Should use find_connections to find the path"
        );

        // Ideally uses a render tool, but plain text response is acceptable
        Assert.IsTrue(
            capture.HasToolCallStartingWith("render_") || capture.FinalResponse is not null,
            "Should render the connection path or provide a text response"
        );

        var eval = await Evaluator.EvaluateAsync(
            "How is Palpatine connected to Luke Skywalker?",
            capture.ToolCalls,
            capture.FinalResponse,
            "Should: 1) search_entities for both Palpatine and Luke Skywalker, "
                + "2) find_connections with both PageIds, 3) render_markdown or render_data_table "
                + "showing the path between them. Multiple search_entities calls are acceptable "
                + "if needed to resolve both entities. Multiple find_connections attempts are acceptable "
                + "if the first attempt used wrong IDs (e.g. Legends vs Canon variant)."
        );

        Assert.IsGreaterThanOrEqualTo(
            2,
            eval.Score,
            $"Evaluator score {eval.Score}/5: {eval.Reasoning}"
        );
    }
}
