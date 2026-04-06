using System.Text.Json.Serialization;

namespace StarWarsData.Services.Executors;

/// <summary>
/// Internal transport type used between workflow executors (Batch → Consolidator → Review).
/// Carries both the LLM-extracted fields AND the narrative fields so thin descriptions
/// survive the pipeline intact.
/// </summary>
internal sealed record ExtractedEvent(
    string EventType,
    string Description,
    string? Narrative,
    string? Significance,
    string? PrecedingContext,
    List<string> Consequences,
    float? Year,
    string? Demarcation,
    string? DateDescription,
    string? Location,
    List<string> RelatedCharacters,
    string SourcePageTitle,
    string SourceWikiUrl
);

/// <summary>
/// Final timeline output schema — used by the review executor's structured output.
/// </summary>
internal sealed record TimelineResponseSchema([property: JsonPropertyName("events")] List<TimelineEventSchema>? Events);

/// <summary>
/// A single event in the final reviewed timeline. Contains both a short headline
/// (<c>description</c>) and a rich narrative body.
/// </summary>
internal sealed record TimelineEventSchema(
    [property: JsonPropertyName("eventType")] string? EventType,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("narrative")] string? Narrative,
    [property: JsonPropertyName("significance")] string? Significance,
    [property: JsonPropertyName("precedingContext")] string? PrecedingContext,
    [property: JsonPropertyName("consequences")] List<string>? Consequences,
    [property: JsonPropertyName("year")] float? Year,
    [property: JsonPropertyName("demarcation")] string? Demarcation,
    [property: JsonPropertyName("dateDescription")] string? DateDescription,
    [property: JsonPropertyName("location")] string? Location,
    [property: JsonPropertyName("relatedCharacters")] List<string>? RelatedCharacters,
    [property: JsonPropertyName("sourcePageTitle")] string? SourcePageTitle,
    [property: JsonPropertyName("sourceWikiUrl")] string? SourceWikiUrl
);

/// <summary>
/// Batch extraction schema — used by the batch extraction executor for multiple pages per LLM call.
/// Each event includes sourcePageTitle so we know which page it came from, and the rich narrative
/// fields that downstream consumers render.
/// </summary>
internal sealed record BatchExtractionSchema([property: JsonPropertyName("events")] List<BatchEventSchema>? Events);

/// <summary>
/// A single event extracted from a batch, with source page attribution and rich narrative fields.
/// </summary>
internal sealed record BatchEventSchema(
    [property: JsonPropertyName("eventType")] string? EventType,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("narrative")] string? Narrative,
    [property: JsonPropertyName("significance")] string? Significance,
    [property: JsonPropertyName("precedingContext")] string? PrecedingContext,
    [property: JsonPropertyName("consequences")] List<string>? Consequences,
    [property: JsonPropertyName("year")] float? Year,
    [property: JsonPropertyName("demarcation")] string? Demarcation,
    [property: JsonPropertyName("dateDescription")] string? DateDescription,
    [property: JsonPropertyName("location")] string? Location,
    [property: JsonPropertyName("relatedCharacters")] List<string>? RelatedCharacters,
    [property: JsonPropertyName("sourcePageTitle")] string? SourcePageTitle,
    [property: JsonPropertyName("sourceWikiUrl")] string? SourceWikiUrl
);
