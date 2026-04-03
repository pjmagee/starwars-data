using StarWarsData.AgentTests.Evaluation;
using StarWarsData.AgentTests.Infrastructure;

namespace StarWarsData.AgentTests.Tests;

[TestClass]
public class TemporalQueryTests
{
    static EvaluatorAgent Evaluator => new(AgentFixture.EvaluatorClient);

    [TestMethod]
    public async Task WhoWasAliveDuringCloneWars_UsesSemanticLifespan()
    {
        var capture = new ConversationCapture();
        await AgentFixture.RunPrompt("Who was alive during the Clone Wars?", capture);

        // Must use find_entities_by_year with lifespan semantic
        // (the AI may skip get_entity_timeline if it already knows the Clone Wars dates)
        Assert.IsTrue(
            capture.HasToolCall("find_entities_by_year"),
            "Should call find_entities_by_year to find characters"
        );

        Assert.IsTrue(
            capture.HasToolCallWithArg("find_entities_by_year", "lifespan"),
            "Should use semantic='lifespan' to find who was alive (not just 'existed')"
        );

        Assert.IsTrue(
            capture.HasToolCallStartingWith("render_"),
            "Should call a render tool to present results"
        );

        var eval = await Evaluator.EvaluateAsync(
            "Who was alive during the Clone Wars?",
            capture.ToolCalls,
            capture.FinalResponse,
            "Should call find_entities_by_year with semantic=lifespan and the Clone Wars year range (~22-19 BBY). "
                + "May optionally call search_entities + get_entity_timeline first to look up the dates, "
                + "or use known dates directly. Must render results."
        );

        Assert.IsGreaterThanOrEqualTo(
            3,
            eval.Score,
            $"Evaluator score {eval.Score}/5: {eval.Reasoning}"
        );
    }

    [TestMethod]
    public async Task GovernmentsIn19BBY_UsesSemanticInstitutional()
    {
        var capture = new ConversationCapture();
        await AgentFixture.RunPrompt("What governments existed in 19 BBY?", capture);

        Assert.IsTrue(
            capture.HasToolCall("find_entities_by_year"),
            "Should call find_entities_by_year"
        );

        Assert.IsTrue(capture.HasToolCallStartingWith("render_"), "Should call a render tool");

        var eval = await Evaluator.EvaluateAsync(
            "What governments existed in 19 BBY?",
            capture.ToolCalls,
            capture.FinalResponse,
            "Should call find_entities_by_year with year=-19, type=Government, "
                + "optionally semantic=institutional. Should render results."
        );

        Assert.IsGreaterThanOrEqualTo(
            3,
            eval.Score,
            $"Evaluator score {eval.Score}/5: {eval.Reasoning}"
        );
    }

    [TestMethod]
    public async Task RiseAndFallOfGalacticRepublic_ReturnsLifecycleChain()
    {
        var capture = new ConversationCapture();
        await AgentFixture.RunPrompt(
            "Tell me about the rise and fall of the Galactic Republic",
            capture
        );

        Assert.IsTrue(
            capture.HasToolCall("search_entities"),
            "Should search for Galactic Republic"
        );

        Assert.IsTrue(
            capture.HasToolCall("get_entity_timeline"),
            "Should get the timeline with temporal facets"
        );

        // Ideally uses a render tool, but plain text response is acceptable
        Assert.IsTrue(
            capture.HasToolCallStartingWith("render_") || capture.FinalResponse is not null,
            "Should render the lifecycle or provide a text response"
        );

        var eval = await Evaluator.EvaluateAsync(
            "Tell me about the rise and fall of the Galactic Republic",
            capture.ToolCalls,
            capture.FinalResponse,
            "Should: 1) search_entities for Galactic Republic, 2) get_entity_timeline to read "
                + "the full institutional lifecycle chain (established, fragmented, reorganized, dissolved, restored), "
                + "3) render_markdown presenting the lifecycle steps in chronological order"
        );

        Assert.IsGreaterThanOrEqualTo(
            3,
            eval.Score,
            $"Evaluator score {eval.Score}/5: {eval.Reasoning}"
        );
    }

    [TestMethod]
    public async Task BooksPublishedIn2015_UsesCEYearAndPublication()
    {
        var capture = new ConversationCapture();
        await AgentFixture.RunPrompt("What Star Wars books were published in 2015?", capture);

        Assert.IsTrue(
            capture.HasToolCall("find_entities_by_year"),
            "Should call find_entities_by_year"
        );

        // Should use CE year 2015, not sort-key -2015
        Assert.IsTrue(
            capture.HasToolCallWithArg("find_entities_by_year", "2015"),
            "Should use CE year 2015"
        );

        Assert.IsTrue(capture.HasToolCallStartingWith("render_"), "Should render results");

        var eval = await Evaluator.EvaluateAsync(
            "What Star Wars books were published in 2015?",
            capture.ToolCalls,
            capture.FinalResponse,
            "Should call find_entities_by_year with year=2015 (CE, not sort-key), type=Book, "
                + "semantic=publication. Must NOT use negative year."
        );

        Assert.IsGreaterThanOrEqualTo(
            3,
            eval.Score,
            $"Evaluator score {eval.Score}/5: {eval.Reasoning}"
        );
    }
}
