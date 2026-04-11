using StarWarsData.Services;
using StarWarsData.Tests.Infrastructure;

namespace StarWarsData.Tests.Agent;

[TestClass]
[TestCategory(TestTiers.Agent)]
[DoNotParallelize]
public class TemporalQueryTests
{
    [ClassInitialize]
    public static async Task ClassSetup(TestContext _) => await AgentFixture.EnsureInitializedAsync();

    private static EvaluatorAgent Evaluator => new(AgentFixture.EvaluatorClient);

    [TestMethod]
    public async Task WhoWasAliveDuringCloneWars_UsesSemanticLifespan()
    {
        var capture = new ConversationCapture();
        await AgentFixture.RunPrompt("Who was alive during the Clone Wars?", capture);

        Assert.IsTrue(capture.HasToolCall(ToolNames.GraphRAG.FindEntitiesByYear), "Should call find_entities_by_year to find characters");
        Assert.IsTrue(capture.HasToolCallWithArg(ToolNames.GraphRAG.FindEntitiesByYear, "lifespan"), "Should use semantic='lifespan' to find who was alive (not just 'existed')");
        Assert.IsTrue(capture.HasToolCallStartingWith("render_"), "Should call a render tool to present results");

        var eval = await Evaluator.EvaluateAsync(
            "Who was alive during the Clone Wars?",
            capture.ToolCalls,
            capture.FinalResponse,
            "Should call find_entities_by_year with semantic=lifespan and the Clone Wars year range (~22-19 BBY). "
                + "May optionally call search_entities + get_entity_timeline first to look up the dates, "
                + "or use known dates directly. Must render results with actual entity names from tool output. "
                + "Score 3+ if the correct temporal tool was used with lifespan semantic and results were rendered. "
                + "Redundant calls are acceptable as long as the final answer uses tool-derived data, not fabrication."
        );

        Assert.IsTrue(eval.Score >= 3, $"Evaluator score {eval.Score}/5: {eval.Reasoning}");
    }

    [TestMethod]
    public async Task GovernmentsIn19BBY_UsesSemanticInstitutional()
    {
        var capture = new ConversationCapture();
        await AgentFixture.RunPrompt("What governments existed in 19 BBY?", capture);

        var toolNames = string.Join(" → ", capture.ToolCalls.Select(t => t.Name));
        var toolArgs = string.Join(
            "\n",
            capture.ToolCalls.Select(t => $"  {t.Name}: args={t.Arguments?[..Math.Min(200, t.Arguments.Length)]} result={t.Result?[..Math.Min(100, t.Result?.Length ?? 0)]}")
        );
        Console.WriteLine($"[DIAG] GovernmentsIn19BBY tool chain: {toolNames}");
        Console.WriteLine($"[DIAG] Tool detail:\n{toolArgs}");
        Console.WriteLine(
            $"[DIAG] FinalResponse null={capture.FinalResponse is null} empty={string.IsNullOrWhiteSpace(capture.FinalResponse)} val='{capture.FinalResponse?[..Math.Min(200, capture.FinalResponse?.Length ?? 0)]}'"
        );

        Assert.IsTrue(capture.HasToolCall(ToolNames.GraphRAG.FindEntitiesByYear), "Should call find_entities_by_year");
        Assert.IsTrue(capture.HasToolCallStartingWith("render_"), "Should call a render tool");

        var eval = await Evaluator.EvaluateAsync(
            "What governments existed in 19 BBY?",
            capture.ToolCalls,
            capture.FinalResponse,
            "Should call find_entities_by_year with year=-19, type=Government, optionally semantic=institutional. Should render results."
        );

        Assert.IsTrue(eval.Score >= 3, $"Evaluator score {eval.Score}/5: {eval.Reasoning}");
    }

    [TestMethod]
    public async Task RiseAndFallOfGalacticRepublic_ReturnsLifecycleChain()
    {
        var capture = new ConversationCapture();
        await AgentFixture.RunPrompt("Tell me about the rise and fall of the Galactic Republic", capture);

        Assert.IsTrue(capture.HasToolCall(ToolNames.GraphRAG.SearchEntities), "Should search for Galactic Republic");
        Assert.IsTrue(
            capture.HasToolCall(ToolNames.GraphRAG.GetEntityTimeline)
                || capture.HasToolCall(ToolNames.GraphRAG.GetEntityProperties)
                || capture.HasToolCall(ToolNames.GraphRAG.GetEntityRelationships)
                || capture.HasToolCall(ToolNames.GraphRAG.SemanticSearch),
            "Should get timeline facets, entity properties, relationships, or semantic context about the lifecycle"
        );

        Assert.IsTrue(capture.HasToolCallStartingWith("render_") || capture.FinalResponse is not null, "Should render the lifecycle or provide a text response");

        var eval = await Evaluator.EvaluateAsync(
            "Tell me about the rise and fall of the Galactic Republic",
            capture.ToolCalls,
            capture.FinalResponse,
            "Should: 1) search_entities for Galactic Republic (which already returns temporal lifecycle facets), "
                + "2) optionally get_entity_timeline for richer lifecycle detail, or use the temporal facets "
                + "from search_entities directly — both approaches are valid, "
                + "3) render_markdown presenting the lifecycle steps in chronological order. "
                + "Score 3+ if the final response covers the Republic's rise and fall with grounded lifecycle dates."
        );

        Assert.IsTrue(eval.Score >= 3, $"Evaluator score {eval.Score}/5: {eval.Reasoning}");
    }

    [TestMethod]
    public async Task BooksPublishedIn2015_UsesCEYearAndPublication()
    {
        var capture = new ConversationCapture();
        await AgentFixture.RunPrompt("What Star Wars books were published in 2015?", capture);

        Assert.IsTrue(capture.HasToolCall(ToolNames.GraphRAG.FindEntitiesByYear), "Should call find_entities_by_year");
        Assert.IsTrue(capture.HasToolCallWithArg(ToolNames.GraphRAG.FindEntitiesByYear, "2015"), "Should use CE year 2015");
        Assert.IsTrue(capture.HasToolCallStartingWith("render_"), "Should render results");

        var eval = await Evaluator.EvaluateAsync(
            "What Star Wars books were published in 2015?",
            capture.ToolCalls,
            capture.FinalResponse,
            "Should call find_entities_by_year with year=2015 (CE, not sort-key), type=Book, semantic=publication. "
                + "Must NOT use negative year. Score 3+ if the correct tool was called with 2015 and results were rendered. "
                + "Extra fallback calls are acceptable if the primary query returned sparse results."
        );

        Assert.IsTrue(eval.Score >= 3, $"Evaluator score {eval.Score}/5: {eval.Reasoning}");
    }
}
