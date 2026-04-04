using System.Text.Json.Serialization;

namespace StarWarsData.Services.Executors;

/// <summary>
/// Final timeline output schema — used by the review executor's structured output.
/// </summary>
internal sealed record TimelineResponseSchema(
    [property: JsonPropertyName("events")] List<TimelineEventSchema>? Events
);

/// <summary>
/// A single event in the final reviewed timeline.
/// </summary>
internal sealed record TimelineEventSchema(
    [property: JsonPropertyName("eventType")] string? EventType,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("year")] float? Year,
    [property: JsonPropertyName("demarcation")] string? Demarcation,
    [property: JsonPropertyName("dateDescription")] string? DateDescription,
    [property: JsonPropertyName("location")] string? Location,
    [property: JsonPropertyName("relatedCharacters")] List<string>? RelatedCharacters,
    [property: JsonPropertyName("sourcePageTitle")] string? SourcePageTitle,
    [property: JsonPropertyName("sourceWikiUrl")] string? SourceWikiUrl
);

/// <summary>
/// Per-page extraction schema — used by the extraction executor for each page.
/// </summary>
internal sealed record PageExtractionSchema(
    [property: JsonPropertyName("events")] List<PageEventSchema>? Events
);

/// <summary>
/// A single event extracted from one page.
/// </summary>
internal sealed record PageEventSchema(
    [property: JsonPropertyName("eventType")] string? EventType,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("year")] float? Year,
    [property: JsonPropertyName("demarcation")] string? Demarcation,
    [property: JsonPropertyName("dateDescription")] string? DateDescription,
    [property: JsonPropertyName("location")] string? Location,
    [property: JsonPropertyName("relatedCharacters")] List<string>? RelatedCharacters
);

/// <summary>
/// Batch extraction schema — used by the batch extraction executor for multiple pages per LLM call.
/// Each event includes sourcePageTitle so we know which page it came from.
/// </summary>
internal sealed record BatchExtractionSchema(
    [property: JsonPropertyName("events")] List<BatchEventSchema>? Events
);

/// <summary>
/// A single event extracted from a batch, with source page attribution.
/// </summary>
internal sealed record BatchEventSchema(
    [property: JsonPropertyName("eventType")] string? EventType,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("year")] float? Year,
    [property: JsonPropertyName("demarcation")] string? Demarcation,
    [property: JsonPropertyName("dateDescription")] string? DateDescription,
    [property: JsonPropertyName("location")] string? Location,
    [property: JsonPropertyName("relatedCharacters")] List<string>? RelatedCharacters,
    [property: JsonPropertyName("sourcePageTitle")] string? SourcePageTitle,
    [property: JsonPropertyName("sourceWikiUrl")] string? SourceWikiUrl
);
