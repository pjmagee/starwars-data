using Microsoft.Extensions.AI;

namespace StarWarsData.AgentTests.Infrastructure;

/// <summary>
/// Records all tool calls made during an agent conversation.
/// Use with IChatClient middleware to capture the full tool call trace.
/// </summary>
public class ConversationCapture
{
    public List<ToolCallRecord> ToolCalls { get; } = [];
    public string? FinalResponse { get; set; }

    public record ToolCallRecord(string Name, string Arguments, string? Result);

    /// <summary>
    /// Creates a delegating chat client middleware that records tool calls.
    /// </summary>
    public Func<IChatClient, IChatClient> CreateMiddleware()
    {
        var capture = this;
        return inner => new CapturingChatClient(inner, capture);
    }

    /// <summary>Check if a tool with the given name was called.</summary>
    public bool HasToolCall(string toolName) => ToolCalls.Any(t => t.Name == toolName);

    /// <summary>Check if any tool starting with the given prefix was called.</summary>
    public bool HasToolCallStartingWith(string prefix) => ToolCalls.Any(t => t.Name.StartsWith(prefix));

    /// <summary>Get the first tool call matching the given name.</summary>
    public ToolCallRecord? GetToolCall(string toolName) => ToolCalls.FirstOrDefault(t => t.Name == toolName);

    /// <summary>Get all tool calls matching the given name.</summary>
    public List<ToolCallRecord> GetToolCalls(string toolName) => ToolCalls.Where(t => t.Name == toolName).ToList();

    /// <summary>Check if a tool call's arguments contain a specific string.</summary>
    public bool HasToolCallWithArg(string toolName, string argSubstring) => ToolCalls.Any(t => t.Name == toolName && t.Arguments.Contains(argSubstring, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Delegating chat client that records function call/result messages.
/// </summary>
internal class CapturingChatClient(IChatClient inner, ConversationCapture capture) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken);

        // Extract tool calls and results from the response messages
        foreach (var message in response.Messages)
        {
            if (message.Role == ChatRole.Assistant)
            {
                foreach (var content in message.Contents.OfType<FunctionCallContent>())
                {
                    var args = content.Arguments is not null ? System.Text.Json.JsonSerializer.Serialize(content.Arguments) : "{}";
                    capture.ToolCalls.Add(new ConversationCapture.ToolCallRecord(content.Name, args, null));
                }

                // Capture final text response
                var text = message.Text;
                if (!string.IsNullOrWhiteSpace(text))
                    capture.FinalResponse = text;
            }

            if (message.Role == ChatRole.Tool)
            {
                foreach (var content in message.Contents.OfType<FunctionResultContent>())
                {
                    // Match result back to the most recent call with same name
                    var matching = capture.ToolCalls.LastOrDefault(t => t.Name == content.CallId || t.Result is null);
                    if (matching is not null)
                    {
                        var resultText = content.Result?.ToString() ?? "";
                        var idx = capture.ToolCalls.IndexOf(matching);
                        capture.ToolCalls[idx] = matching with { Result = resultText };
                    }
                }
            }
        }

        return response;
    }
}
