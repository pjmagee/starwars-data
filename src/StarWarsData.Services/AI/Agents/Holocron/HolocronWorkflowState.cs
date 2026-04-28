using StarWarsData.Models;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services.AI.Agents.Holocron;

// ── Serializable state DTOs persisted into workflow context ────────────────
// All shapes used for context.QueueStateUpdateAsync / context.ReadStateAsync
// plus the per-batch progress / checkpoint records. Keeping these in one file
// makes the inter-executor data flow easy to audit.

/// <summary>
/// The target node's identity + canonical-truth properties, snapshotted at
/// <c>HolocronContextDiscoveryExecutor</c> time so downstream executors don't
/// need to re-query <c>kg.nodes</c>. Includes the <c>contentHash</c> stamped
/// onto every enrichment created during the run for staleness detection.
/// </summary>
public sealed record HolocronNodeSnapshot(
    int PageId,
    string Name,
    string Type,
    string ContentHash,
    string WikiUrl,
    Continuity Continuity,
    Realm Realm,
    int? StartYear,
    int? EndYear,
    Dictionary<string, List<string>> Properties,
    List<TemporalFacetSnapshot> TemporalFacets
);

/// <summary>
/// Subset of <see cref="TemporalFacet"/> the prompt builder needs. Stripped of
/// id / metadata fields that complicate JSON round-tripping inside the
/// checkpoint store.
/// </summary>
public sealed record TemporalFacetSnapshot(string Semantic, string Calendar, int? Year, string Text);

/// <summary>
/// Lightweight neighbour record for the prompt — only what the agent actually
/// needs to reason about cross-page context.
/// </summary>
public sealed record HolocronNeighbour(int PageId, string Name, string Type, int? StartYear, int? EndYear);

/// <summary>
/// Top-K canonical edge label for the target's type. Surfaced to the agent so
/// the <c>addEdges</c> array can only use known synonyms — pre-flight rejects
/// anything else.
/// </summary>
public sealed record HolocronCanonicalLabel(string Label, string Reverse, string Description, int UsageCount);

/// <summary>
/// One backlink chunk surviving the discovery + skip-if-unchanged filter, ready
/// to be bundled and fed to the LLM. Carries <c>contentHash</c> so the apply
/// step can record it in the per-(node, chunk) ledger after processing.
///
/// <b>Used at LLM-call time only.</b> The full <see cref="Text"/> is heavy
/// (avg 4–8 KB / chunk; 2520 chunks for Anakin = ~15 MB) so we deliberately
/// keep this OUT of workflow state, where it would balloon the framework
/// checkpoint past Mongo's 16 MB document limit. Workflow state carries
/// <see cref="HolocronChunkRef"/> instead; the extractor rehydrates the text
/// per-batch via a single <c>Find({_id: $in: [...]})</c>.
/// </summary>
public sealed record HolocronChunkPayload(string ChunkId, int PageId, string Title, string Heading, string Section, string Text, string ContentHash, List<string> Links);

/// <summary>
/// Lightweight chunk reference stored in workflow state in place of
/// <see cref="HolocronChunkPayload"/>. ~200 bytes / record (no Text), so even
/// Sidious-class nodes (1,686 chunks) fit well under Mongo's 16 MB checkpoint
/// document limit.
/// <para>
/// <see cref="TextLength"/> lets the bundler partition by char-budget without
/// having the actual text in memory at bundle time — the discovery executor
/// reads each chunk's text once, computes its length, then drops the body.
/// </para>
/// </summary>
public sealed record HolocronChunkRef(string ChunkId, int PageId, string Title, string Heading, string Section, string ContentHash, int TextLength);

/// <summary>
/// One LLM-batch worth of chunk references. Index is monotonic across the run so
/// the extractor can track which batches are done in <c>HolocronExtractionProgress</c>.
/// The extractor rehydrates each batch's text content from <c>search.chunks</c>
/// at LLM-call time (cheap — bulk find by indexed _id).
/// </summary>
public sealed record HolocronBatch(int BatchIndex, List<HolocronChunkRef> Chunks);

/// <summary>
/// One raw <c>annotateEdges</c> proposal carried across executors. Mirrors the
/// agent's structured-output shape but stored as a serializable record so it
/// round-trips through <see cref="MongoCheckpointStore"/> cleanly.
/// </summary>
public sealed record HolocronAnnotateProposal(
    int BatchIndex,
    int FromId,
    int ToId,
    string Label,
    string? Role,
    string? Qualifier,
    string? Description,
    string Claim,
    List<HolocronEvidencePayload> Evidence,
    string Reasoning
);

public sealed record HolocronFillGapProposal(int BatchIndex, int FromId, int ToId, string Label, int? FromYear, int? ToYear, string Claim, List<HolocronEvidencePayload> Evidence, string Reasoning);

public sealed record HolocronAddEdgeProposal(
    int BatchIndex,
    int FromId,
    int ToId,
    string Label,
    int? FromYear,
    int? ToYear,
    double? Weight,
    string Claim,
    List<HolocronEvidencePayload> Evidence,
    string Reasoning
);

public sealed record HolocronNodeProposalPayload(int BatchIndex, string FieldPath, List<string> Values, string Claim, List<HolocronEvidencePayload> Evidence, string Reasoning);

public sealed record HolocronEvidencePayload(int? SourcePageId, string? ChunkId, string Excerpt, double? RelevanceScore);

/// <summary>
/// Aggregated raw proposals across every batch. Produced by the extractor,
/// consumed by the consolidator. Stays in workflow state so a resumed run after
/// the consolidator stage doesn't lose intermediate state.
/// </summary>
public sealed record HolocronRawProposalSet(
    List<HolocronAnnotateProposal> AnnotateEdges,
    List<HolocronFillGapProposal> FillGapEdges,
    List<HolocronAddEdgeProposal> AddEdges,
    List<HolocronNodeProposalPayload> NodeProposals
);

/// <summary>
/// Output of <c>HolocronConsolidatorExecutor</c> — proposals that survived
/// cross-batch deduplication + pre-flight rule application. The apply step
/// writes these directly to <c>kg.enrichments</c> / <c>kg.edge_enrichments</c>.
/// </summary>
public sealed record HolocronConsolidatedProposals(
    List<HolocronAnnotateProposal> AnnotateEdges,
    List<HolocronFillGapProposal> FillGapEdges,
    List<HolocronAddEdgeProposal> AddEdges,
    List<HolocronNodeProposalPayload> NodeProposals,
    int DuplicatesDropped,
    int PreflightRejects,
    int EvidenceFailures
);

/// <summary>
/// In-memory checkpoint mirrored at superstep boundaries by the extractor's
/// <c>OnCheckpointingAsync</c>. Same pattern as Character Timeline's
/// <c>BatchExtractionCheckpoint</c> — index of processed batches plus the
/// accumulated raw proposals so a resume picks up where the failed run left off.
/// </summary>
public sealed record HolocronExtractionCheckpoint(List<int> ProcessedBatchIndices, HolocronRawProposalSet Proposals);
