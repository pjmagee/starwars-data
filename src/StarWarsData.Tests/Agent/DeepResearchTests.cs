using StarWarsData.Services;
using StarWarsData.Tests.Infrastructure;

namespace StarWarsData.Tests.Agent;

[TestClass]
[TestCategory(TestTiers.Agent)]
[DoNotParallelize]
public class DeepResearchTests
{
    [ClassInitialize]
    public static async Task ClassSetup(TestContext _) => await AgentFixture.EnsureInitializedAsync();

    private static EvaluatorAgent Evaluator => new(AgentFixture.EvaluatorClient);

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task RevanHolocronToBrotherhoodToRuusanReformation_MultiEntityChain()
    {
        var capture = new ConversationCapture();
        await AgentFixture.RunPrompt(
            "Trace the chain of events from Darth Revan's Sith Holocron to the destruction of the "
                + "Brotherhood of Darkness: what knowledge did the holocron contain, who used it, how did "
                + "that lead to the Seventh Battle of Ruusan, and what political reforms resulted from the "
                + "aftermath — including how those reforms ultimately shaped the Galactic Republic that "
                + "Palpatine later dissolved?",
            capture
        );

        var searchCalls = capture.GetToolCalls(ToolNames.GraphRAG.SearchEntities);
        Assert.IsTrue(searchCalls.Count >= 2, $"Should search for multiple entities, got {searchCalls.Count} search_entities calls");

        Assert.IsTrue(
            capture.HasToolCall(ToolNames.GraphRAG.GetEntityTimeline)
                || capture.HasToolCall(ToolNames.GraphRAG.FindEntitiesByYear)
                || capture.HasToolCall(ToolNames.GraphRAG.GetEntityProperties)
                || capture.HasToolCall(ToolNames.GraphRAG.GetEntityRelationships),
            "Should use temporal or relationship tools to look up lifecycles, date ranges, or chain connections"
        );

        Assert.IsTrue(
            capture.HasToolCall(ToolNames.GraphRAG.GetEntityRelationships)
                || capture.HasToolCall(ToolNames.GraphRAG.TraverseGraph)
                || capture.HasToolCall(ToolNames.GraphRAG.FindConnections)
                || capture.HasToolCall(ToolNames.GraphRAG.SemanticSearch),
            "Should use relationship or article search tools to trace the chain"
        );

        Assert.IsTrue(capture.HasToolCallStartingWith("render_") || capture.FinalResponse is not null, "Should render results or provide a text response");

        Assert.IsTrue(capture.ToolCalls.Count >= 4, $"Complex question should require 4+ tool calls, got {capture.ToolCalls.Count}");

        var eval = await Evaluator.EvaluateAsync(
            "Trace Darth Revan's Sith Holocron → Brotherhood of Darkness → Seventh Battle of Ruusan → political reforms → Palpatine",
            capture.ToolCalls,
            capture.FinalResponse,
            """
            This is a complex multi-entity research question spanning 6+ entity types and 25,000 years.
            The ideal answer chain:
            1. Darth Revan's Sith Holocron (HolocronInfobox) — contains thought bomb knowledge
            2. Darth Bane (Character) — studied the holocron, was in Brotherhood of Darkness
            3. Brotherhood of Darkness (Organization) — dissolved 1000 BBY at Ruusan
            4. Seven Battles of Ruusan (Battle) — culminating in the Seventh Battle where the thought bomb was detonated
            5. Ruusan Reformation (Event/Law) — political reforms in 1000 BBY that restructured the Republic
            6. Galactic Republic (Government) — reorganized at 1000 BBY, then again at 19 BBY into the Empire

            The agent should:
            - Search for multiple entities across different types (not just Characters)
            - Use get_entity_timeline or get_entity_relationships to trace connections
            - Use semantic_search for narrative depth on the chain of events
            - Use temporal data (institutional.reorganized facets on the Republic, conflict.point on battles)
            - Present a coherent narrative connecting ancient Sith knowledge to galactic political change

            Score 5: Traces the full chain with evidence from multiple entity types and temporal facets
            Score 4: Gets most of the chain but misses one link (e.g. the holocron or the Reformation)
            Score 3: Covers the major events but doesn't fully connect the chain
            Score 2: Only covers part of the question (e.g. just Bane or just Ruusan)
            Score 1: Wrong approach or fabricated connections
            """
        );

        Assert.IsTrue(eval.Score >= 2, $"Evaluator score {eval.Score}/5: {eval.Reasoning}");

        Console.WriteLine($"=== Deep Research Evaluation ===");
        Console.WriteLine($"Score: {eval.Score}/5");
        Console.WriteLine($"Pass: {eval.Pass}");
        Console.WriteLine($"Reasoning: {eval.Reasoning}");
        Console.WriteLine($"Issues: {string.Join("; ", eval.Issues)}");
        Console.WriteLine($"Tool calls made: {capture.ToolCalls.Count}");
        foreach (var tc in capture.ToolCalls)
            Console.WriteLine($"  - {tc.Name}({(tc.Arguments.Length > 100 ? tc.Arguments[..100] + "..." : tc.Arguments)})");
    }
}
