using System.Text.Json.Serialization;

namespace StarWarsData.Services;

/// <summary>
/// Structured output schema for single-turn relationship extraction.
/// Used with ChatResponseFormat.ForJsonSchema to constrain the LLM response.
/// </summary>
internal sealed record RelationshipExtractionResponse(
    [property: JsonPropertyName("shouldSkip")] bool ShouldSkip,
    [property: JsonPropertyName("skipReason")] string? SkipReason,
    [property: JsonPropertyName("edges")] List<ExtractedEdge>? Edges
);

internal sealed record ExtractedEdge(
    [property: JsonPropertyName("fromId")] int FromId,
    [property: JsonPropertyName("fromName")] string FromName,
    [property: JsonPropertyName("fromType")] string FromType,
    [property: JsonPropertyName("toId")] int ToId,
    [property: JsonPropertyName("toName")] string ToName,
    [property: JsonPropertyName("toType")] string ToType,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("reverseLabel")] string ReverseLabel,
    [property: JsonPropertyName("weight")] double Weight,
    [property: JsonPropertyName("evidence")] string Evidence,
    [property: JsonPropertyName("continuity")] string Continuity
);
