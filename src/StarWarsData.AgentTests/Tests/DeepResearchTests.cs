using StarWarsData.AgentTests.Evaluation;
using StarWarsData.AgentTests.Infrastructure;

namespace StarWarsData.AgentTests.Tests;

[TestClass]
public class DeepResearchTests
{
    static EvaluatorAgent Evaluator => new(AgentFixture.EvaluatorClient);

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)] // 2 min — this is a complex multi-step query
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

        // Must search for multiple entities across different types
        var searchCalls = capture.GetToolCalls("search_entities");
        Assert.IsGreaterThanOrEqualTo(searchCalls.Count, 2, $"Should search for multiple entities, got {searchCalls.Count} search_entities calls");

        // Must use at least one temporal tool (get_entity_timeline or find_entities_by_year)
        Assert.IsTrue(capture.HasToolCall("get_entity_timeline") || capture.HasToolCall("find_entities_by_year"), "Should use temporal tools to look up lifecycles or date ranges");

        // Must use relationship or article tools for the connection chain
        Assert.IsTrue(
            capture.HasToolCall("get_entity_relationships") || capture.HasToolCall("traverse_graph") || capture.HasToolCall("find_connections") || capture.HasToolCall("semantic_search"),
            "Should use relationship or article search tools to trace the chain"
        );

        // Must produce some output
        Assert.IsTrue(capture.HasToolCallStartingWith("render_") || capture.FinalResponse is not null, "Should render results or provide a text response");

        // Must make at least 4 tool calls total for a question this complex
        Assert.IsGreaterThanOrEqualTo(capture.ToolCalls.Count, 4, $"Complex question should require 4+ tool calls, got {capture.ToolCalls.Count}");

        // LLM evaluator with detailed rubric
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

        Assert.IsGreaterThanOrEqualTo(2, eval.Score, $"Evaluator score {eval.Score}/5: {eval.Reasoning}");

        // Log the full evaluation for manual review
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
