using StarWarsData.Services;
using StarWarsData.Tests.Infrastructure;

namespace StarWarsData.Tests.Agent;

/// <summary>
/// Integration tests that validate the full Ask AI pipeline end-to-end against the
/// same tool surface, system prompt, and chat client the production ApiService
/// wires up. Uses <see cref="AgentFixture"/> (ComponentToolkit + DataExplorerToolkit
/// + GraphRAGToolkit + KGAnalytics + wiki keyword_search) against a live
/// <c>starwars-dev</c> MongoDB and real OpenAI.
///
/// Requires environment variables:
///   STARWARS_OPENAI_KEY         — OpenAI API key
///   MDB_MCP_CONNECTION_STRING   — MongoDB connection string
/// </summary>
[TestClass]
[TestCategory(TestTiers.Agent)]
[DoNotParallelize]
public class AskAiPipelineTests
{
    [ClassInitialize]
    public static async Task ClassSetup(TestContext _) => await AgentFixture.EnsureInitializedAsync();

    private static EvaluatorAgent Evaluator => new(AgentFixture.EvaluatorClient);

    [TestMethod]
    public async Task Ask_ForcePowersByAlignment_ReturnsChart()
    {
        var capture = new ConversationCapture();
        await AgentFixture.RunPrompt("How many Force powers are light side vs dark side vs universal? Show as a bar chart.", capture);

        var toolNames = string.Join(" → ", capture.ToolCalls.Select(t => t.Name));
        Console.WriteLine($"[DIAG] ForcePowersByAlignment tool chain: {toolNames}");
        Console.WriteLine(
            $"[DIAG] FinalResponse null={capture.FinalResponse is null} empty={string.IsNullOrWhiteSpace(capture.FinalResponse)} val='{capture.FinalResponse?[..Math.Min(200, capture.FinalResponse?.Length ?? 0)]}'"
        );
        Assert.IsTrue(capture.HasToolCall(ToolNames.KGAnalytics.CountNodesByProperty), "Should aggregate Force powers by Alignment via count_nodes_by_property");
        Assert.IsTrue(capture.HasToolCall(ToolNames.Component.RenderChart), $"Should finish with render_chart. Actual calls: {toolNames}");

        var eval = await Evaluator.EvaluateAsync(
            "How many Force powers are light side vs dark side vs universal? Show as a bar chart.",
            capture.ToolCalls,
            capture.FinalResponse,
            "Should: 1) use count_nodes_by_property (or count_nodes_by_properties) over ForcePower entities grouped by the "
                + "Alignment property, 2) render_chart with chartType=Bar showing alignment categories "
                + "and values sourced from the aggregation. The KG has more Alignment values than just light/dark/"
                + "universal — including all alignment categories returned by the tool is correct behaviour, "
                + "not a penalty. Score 3+ if the aggregation was called and a Bar chart was rendered with real counts."
        );

        Assert.IsTrue(eval.Score >= 3, $"Evaluator score {eval.Score}/5: {eval.Reasoning}");
    }

    [TestMethod]
    public async Task Ask_CharacterRelationshipGraph_ReturnsGraph()
    {
        var capture = new ConversationCapture();
        await AgentFixture.RunPrompt("Show me Luke Skywalker's relationship graph.", capture);

        Assert.IsTrue(capture.HasToolCall(ToolNames.GraphRAG.SearchEntities), "Should resolve Luke Skywalker's PageId via search_entities");
        Assert.IsTrue(capture.HasToolCall(ToolNames.Component.RenderGraph), "Should finish with render_graph");

        var eval = await Evaluator.EvaluateAsync(
            "Show me Luke Skywalker's relationship graph.",
            capture.ToolCalls,
            capture.FinalResponse,
            "Should: 1) search_entities for Luke Skywalker to get the Character PageId, "
                + "2) get_relationship_types to discover which KG edge labels exist for that entity, "
                + "3) render_graph with the discovered labels and a sensible maxDepth. Must root on a Character, "
                + "not a Family or Homeworld entity. Hard-coding labels without discovery is penalised."
        );

        Assert.IsTrue(eval.Score >= 3, $"Evaluator score {eval.Score}/5: {eval.Reasoning}");
    }

    [TestMethod]
    public async Task Ask_BattlesByEra_ReturnsChart()
    {
        var capture = new ConversationCapture();
        await AgentFixture.RunPrompt("How many battles occurred per era? Show as a pie chart.", capture);

        var toolNames = string.Join(" → ", capture.ToolCalls.Select(t => t.Name));
        var toolArgsDetails = string.Join(
            "\n",
            capture.ToolCalls.Select(t => $"  {t.Name}: args={t.Arguments?[..Math.Min(300, t.Arguments.Length)]} result={t.Result?[..Math.Min(100, t.Result?.Length ?? 0)]}")
        );
        Console.WriteLine($"[DIAG] BattlesByEra tool chain: {toolNames}");
        Console.WriteLine($"[DIAG] Tool detail:\n{toolArgsDetails}");
        Console.WriteLine(
            $"[DIAG] FinalResponse null={capture.FinalResponse is null} empty={string.IsNullOrWhiteSpace(capture.FinalResponse)} val='{capture.FinalResponse?[..Math.Min(200, capture.FinalResponse?.Length ?? 0)]}'"
        );
        Assert.IsTrue(
            capture.HasToolCall(ToolNames.KGAnalytics.CountNodesByProperty) || capture.HasToolCall(ToolNames.KGAnalytics.CountByYearRange),
            $"Should aggregate battles by era via count_nodes_by_property or a year-range count. Actual calls: {toolNames}"
        );
        Assert.IsTrue(capture.HasToolCall(ToolNames.Component.RenderChart), $"Should finish with render_chart. Actual calls: {toolNames}");

        var eval = await Evaluator.EvaluateAsync(
            "How many battles occurred per era? Show as a pie chart.",
            capture.ToolCalls,
            capture.FinalResponse,
            "Should: 1) aggregate Battle entities by era or time period — via count_nodes_by_property('Battle','Era') "
                + "or count_by_year_range to partition battles into year-range buckets. Year-range bucket labels "
                + "(e.g. '5000 BBY', '0 BBY/ABY') are acceptable since the KG stores year data, not named eras. "
                + "2) render_chart with chartType=Pie showing the distribution. "
                + "Counts that appear after count_by_year_range was called are from the tool output and are NOT fabricated. "
                + "Score 3+ if the agent aggregated battles by time period and rendered a Pie chart."
        );

        Assert.IsTrue(eval.Score >= 3, $"Evaluator score {eval.Score}/5: {eval.Reasoning}");
    }
}
