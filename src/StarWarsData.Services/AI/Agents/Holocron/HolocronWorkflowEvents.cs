using Microsoft.Agents.AI.Workflows;

namespace StarWarsData.Services.AI.Agents.Holocron;

// ── Custom WorkflowEvent types emitted by Holocron pipeline executors ──────
// Consumed via StreamingRun.WatchStreamAsync() and bridged to the tracker so
// the UI gets a per-stage activity feed identical in shape to the Character
// Timeline pipeline (which is the canonical reference implementation).

/// <summary>
/// Emitted by <c>HolocronContextDiscoveryExecutor</c> after the backlink-chunk
/// query + ledger comparison completes. Reports the full corpus size and
/// what slipped through unchanged-skip filtering.
/// </summary>
public sealed class HolocronDiscoveryCompleteEvent(HolocronDiscoveryCompleteData data) : WorkflowEvent(data);

public sealed record HolocronDiscoveryCompleteData(int TotalChunks, int NewChunks, int SkippedUnchanged, int LinkingPages, int OutEdges, int InEdges, int Neighbours, int CanonicalLabels);

/// <summary>
/// Emitted by <c>HolocronBundlerExecutor</c> when the new-chunk set has been
/// grouped into LLM-batches.
/// </summary>
public sealed class HolocronBundlingCompleteEvent(HolocronBundlingCompleteData data) : WorkflowEvent(data);

public sealed record HolocronBundlingCompleteData(int TotalChunks, int BatchCount, List<int> BatchSizes);

/// <summary>
/// Emitted by <c>HolocronProposalExtractorExecutor</c> at the start of each batch
/// LLM call so the activity log shows real-time progress.
/// </summary>
public sealed class HolocronBatchExtractionStartedEvent(HolocronBatchExtractionStartedData data) : WorkflowEvent(data);

public sealed record HolocronBatchExtractionStartedData(int BatchIndex, int TotalBatches, int ChunkCount, List<string> SourcePageTitles);

/// <summary>
/// Emitted when a batch yields zero proposals (rare, but worth surfacing — usually
/// means the chunks happened to be filler from related-but-unrelated pages).
/// </summary>
public sealed class HolocronBatchExtractionEmptyEvent(HolocronBatchExtractionEmptyData data) : WorkflowEvent(data);

public sealed record HolocronBatchExtractionEmptyData(int BatchIndex, int ChunkCount);

/// <summary>
/// Emitted when the LLM call for a batch fails. The pipeline marks the batch as
/// processed (so we don't loop on it forever) and continues — surfaced here so
/// the user can see which chunks didn't make it through.
/// </summary>
public sealed class HolocronBatchExtractionFailedEvent(HolocronBatchExtractionFailedData data) : WorkflowEvent(data);

public sealed record HolocronBatchExtractionFailedData(int BatchIndex, int ChunkCount, string Error);

/// <summary>
/// Emitted after a batch successfully produces structured proposals. Counters
/// per-array let the UI show "this batch found 3 annotates, 1 add, 2 augments".
/// </summary>
public sealed class HolocronProposalsExtractedEvent(HolocronProposalsExtractedData data) : WorkflowEvent(data);

public sealed record HolocronProposalsExtractedData(int BatchIndex, int Annotates, int FillGaps, int AddEdges, int NodeProposals);

/// <summary>
/// Emitted by <c>HolocronConsolidatorExecutor</c> after cross-batch deduplication
/// + pre-flight rule application. Reports what survived to the apply step.
/// </summary>
public sealed class HolocronConsolidationCompleteEvent(HolocronConsolidationCompleteData data) : WorkflowEvent(data);

public sealed record HolocronConsolidationCompleteData(int RawProposals, int Consolidated, int DuplicatesDropped, int PreflightRejects, int EvidenceFailures);

/// <summary>
/// Emitted by <c>HolocronApplyExecutor</c> with the final write summary. Last
/// event of every successful run.
/// </summary>
public sealed class HolocronApplyCompleteEvent(HolocronApplyCompleteData data) : WorkflowEvent(data);

public sealed record HolocronApplyCompleteData(int EnrichmentsWritten, int EdgeEnrichmentsWritten, int EventsEmitted, int ProcessedChunksRecorded);
