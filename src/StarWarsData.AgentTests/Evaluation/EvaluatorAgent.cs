using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using StarWarsData.AgentTests.Infrastructure;

namespace StarWarsData.AgentTests.Evaluation;

public record EvaluationResult(bool Pass, int Score, string Reasoning, List<string> Issues);

/// <summary>
/// LLM-based evaluator that scores agent conversations for quality.
/// Uses structured output (JSON schema) for reliable parsing.
/// </summary>
public class EvaluatorAgent(IChatClient client)
{
    const string EvaluatorPrompt = """
        You are a QA evaluator for a Star Wars data assistant. You will be given:
        1. A user prompt
        2. The tool calls the assistant made (name + arguments)
        3. The assistant's final response (if any)
        4. A rubric describing what the correct behavior should be

        Evaluate whether:
        - The correct tools were called for this query type
        - Tool parameters are reasonable and well-formed
        - The tool call sequence is efficient (minimal unnecessary calls)
        - The final response uses an appropriate render tool
        - The assistant did not fabricate data

        Scoring guide:
        5 = Perfect: correct tools, efficient sequence, appropriate render
        4 = Good: correct tools with minor inefficiency
        3 = Acceptable: mostly correct but missed an optimization or used a suboptimal tool
        2 = Poor: wrong tools or missing key steps
        1 = Fail: completely wrong approach or fabricated data
        """;

    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    static readonly ChatOptions EvalOptions = new()
    {
        Temperature = 0f,
        ResponseFormat = ChatResponseFormat.ForJsonSchema<EvalSchema>(schemaName: "evaluation_result", schemaDescription: "QA evaluation of an AI agent conversation"),
    };

    public async Task<EvaluationResult> EvaluateAsync(string userPrompt, List<ConversationCapture.ToolCallRecord> toolCalls, string? finalResponse, string rubric)
    {
        var toolCallSummary = string.Join("\n", toolCalls.Select((t, i) => $"  {i + 1}. {t.Name}({TruncateArgs(t.Arguments, 200)})"));

        var evalPrompt = $"""
            USER PROMPT: {userPrompt}

            TOOL CALLS MADE:
            {toolCallSummary}

            FINAL RESPONSE: {Truncate(finalResponse ?? "(none)", 500)}

            RUBRIC: {rubric}
            """;

        var messages = new List<ChatMessage> { new(ChatRole.System, EvaluatorPrompt), new(ChatRole.User, evalPrompt) };

        var response = await client.GetResponseAsync(messages, EvalOptions);
        var text = response.Text ?? "{}";

        try
        {
            var result = JsonSerializer.Deserialize<EvalSchema>(text, JsonOpts);
            return new EvaluationResult(result?.Pass ?? false, result?.Score ?? 1, result?.Reasoning ?? "Failed to parse evaluation", result?.Issues ?? []);
        }
        catch
        {
            return new EvaluationResult(false, 1, $"Failed to parse evaluator response: {text}", []);
        }
    }

    /// <summary>Structured output schema for the evaluator response.</summary>
    sealed record EvalSchema(
        [property: JsonPropertyName("pass")] bool Pass,
        [property: JsonPropertyName("score")] int Score,
        [property: JsonPropertyName("reasoning")] string Reasoning,
        [property: JsonPropertyName("issues")] List<string> Issues
    );

    static string TruncateArgs(string args, int max) => args.Length > max ? args[..(max - 1)] + "..." : args;

    static string Truncate(string s, int max) => s.Length > max ? s[..(max - 1)] + "..." : s;
}
