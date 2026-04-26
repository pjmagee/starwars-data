namespace StarWarsData.ApiService.Features.Holocron;

/// <summary>
/// One event on the Holocron Log page. Flattened shape with the source node's
/// <c>Name</c> joined in (so the frontend doesn't need a second roundtrip per row).
/// PageId / FromId / ToId / Label / FieldPath / EnrichmentId are all optional —
/// pass-bookkeeping events (<c>HolocronPassStarted</c>, <c>HolocronPassCompleted</c>)
/// don't carry an enrichment pointer.
/// </summary>
public sealed record HolocronEventDto(
    string Id,
    string EventType,
    string Summary,
    DateTime OccurredAt,
    int? PageId,
    string? NodeName,
    string? FieldPath,
    int? FromId,
    int? ToId,
    string? FromName,
    string? ToName,
    string? Label,
    string? EnrichmentId,
    string TriggeredBy,
    string AgentVersion
);

public sealed record HolocronEventsPage(List<HolocronEventDto> Items, int Total, int Page, int PageSize);

/// <summary>
/// Full enrichment detail surfaced when the user expands a row. Carries the
/// claim + evidence + reasoning that the events row's summary glosses over.
/// Used for both <c>NodeEnrichment</c> and <c>EdgeEnrichment</c> — <see cref="Kind"/>
/// distinguishes them.
/// </summary>
public sealed record HolocronEnrichmentDetailDto(
    string Id,
    string Kind, // "node" | "edge"
    int? PageId,
    string? FieldPath,
    int? FromId,
    int? ToId,
    string? Label,
    string Operation,
    string ValueJson,
    string Claim,
    List<HolocronEvidenceDto> Evidence,
    string? LlmReasoning,
    string Status,
    DateTime CreatedAt,
    string AgentVersion,
    string ModelId,
    string? NodeName,
    string? FromName,
    string? ToName
);

public sealed record HolocronEvidenceDto(int? SourcePageId, string? SourcePageName, string? ChunkId, string Excerpt, double? RelevanceScore);
