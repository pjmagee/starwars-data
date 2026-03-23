using Microsoft.Agents.AI.Workflows;

namespace StarWarsData.Services.Executors;

// ── Custom WorkflowEvent types emitted by timeline executors ──────────────
// Consumed via StreamingRun.WatchStreamAsync() and bridged to the tracker.

/// <summary>
/// Emitted by PageDiscoveryExecutor for each page discovered during research.
/// </summary>
internal sealed class PageDiscoveredEvent(PageDiscoveredData data) : WorkflowEvent(data);

internal sealed record PageDiscoveredData(
    int PageId,
    string Title,
    string WikiUrl,
    string? Template,
    string Continuity,
    string Relationship); // "self", "incoming", "outgoing"

/// <summary>
/// Emitted by PageDiscoveryExecutor when discovery is complete with summary.
/// </summary>
internal sealed class DiscoveryCompleteEvent(DiscoveryCompleteData data) : WorkflowEvent(data);

internal sealed record DiscoveryCompleteData(
    int TotalPages,
    int IncomingLinks,
    int OutgoingLinks);

/// <summary>
/// Emitted by EventExtractionExecutor when starting extraction from a page.
/// </summary>
internal sealed class ExtractionPageStartedEvent(ExtractionPageStartedData data) : WorkflowEvent(data);

internal sealed record ExtractionPageStartedData(
    string PageTitle,
    int PageIndex,
    int TotalPages);

/// <summary>
/// Emitted by EventExtractionExecutor for each event found during extraction.
/// </summary>
internal sealed class EventExtractedEvent(EventExtractedData data) : WorkflowEvent(data);

internal sealed record EventExtractedData(
    string EventType,
    string Description,
    float? Year,
    string? Demarcation,
    string SourcePageTitle);

/// <summary>
/// Emitted by EventExtractionExecutor when a page yields no events.
/// </summary>
internal sealed class ExtractionPageEmptyEvent(string pageTitle) : WorkflowEvent(pageTitle);

/// <summary>
/// Emitted by EventExtractionExecutor when a page extraction fails.
/// </summary>
internal sealed class ExtractionPageFailedEvent(ExtractionPageFailedData data) : WorkflowEvent(data);

internal sealed record ExtractionPageFailedData(
    string PageTitle,
    string Error);

/// <summary>
/// Emitted by PageBundlerExecutor when bundling is complete.
/// </summary>
internal sealed class BundlingCompleteEvent(BundlingCompleteData data) : WorkflowEvent(data);

internal sealed record BundlingCompleteData(
    int TotalPages,
    int BatchCount,
    List<int> BatchSizes);

/// <summary>
/// Emitted by BatchExtractionExecutor when starting a batch.
/// </summary>
internal sealed class BatchExtractionStartedEvent(BatchExtractionStartedData data) : WorkflowEvent(data);

internal sealed record BatchExtractionStartedData(
    int BatchIndex,
    int TotalBatches,
    int PageCount,
    List<string> PageTitles);

/// <summary>
/// Emitted by BatchExtractionExecutor when a batch yields no events.
/// </summary>
internal sealed class BatchExtractionEmptyEvent(BatchExtractionEmptyData data) : WorkflowEvent(data);

internal sealed record BatchExtractionEmptyData(
    int BatchIndex,
    int PageCount);

/// <summary>
/// Emitted by BatchExtractionExecutor when a batch extraction fails.
/// </summary>
internal sealed class BatchExtractionFailedEvent(BatchExtractionFailedData data) : WorkflowEvent(data);

internal sealed record BatchExtractionFailedData(
    int BatchIndex,
    int PageCount,
    string Error);

/// <summary>
/// Emitted by EventConsolidatorExecutor with consolidation summary.
/// </summary>
internal sealed class ConsolidationCompleteEvent(ConsolidationCompleteData data) : WorkflowEvent(data);

internal sealed record ConsolidationCompleteData(
    int InputEventCount,
    int OutputEventCount,
    int DuplicatesRemoved);

/// <summary>
/// Emitted by EventReviewExecutor with the review summary.
/// </summary>
internal sealed class ReviewCompleteEvent(ReviewCompleteData data) : WorkflowEvent(data);

internal sealed record ReviewCompleteData(
    int InputEventCount,
    int OutputEventCount,
    int EventsRemoved);
