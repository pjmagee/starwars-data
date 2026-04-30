using System.Text.Json.Serialization;

namespace StarWarsData.Frontend.Components.Shared.Agui;

/// <summary>
/// One AGUI conversation message replayed to the streaming endpoint each turn.
/// Wire-format compatible with both <c>/kernel/stream</c> (AskAI) and
/// <c>/copilot/stream</c> (Copilot sidebar).
/// </summary>
public sealed class AguiMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("toolCalls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AguiToolCall>? ToolCalls { get; set; }

    [JsonPropertyName("toolCallId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; set; }
}

public sealed class AguiToolCall
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public AguiFunction Function { get; set; } = new();
}

public sealed class AguiFunction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = "";
}
