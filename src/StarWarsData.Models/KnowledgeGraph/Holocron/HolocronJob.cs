using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Entities;

/// <summary>
/// One Holocron enhance request, tracked through its 5-executor lifecycle. Stored in
/// <c>kg.enrichment_jobs</c> per Design-020. The async kickoff pattern: the API
/// returns the <see cref="Id"/> immediately on enqueue, the Hangfire worker
/// transitions <see cref="Status"/> through Discovering → Bundling → Extracting →
/// Consolidating → Applying → Completed (or → Failed on error), and the UI polls
/// for the live counters.
///
/// Invariant: at most one <c>Queued</c>/<c>Discovering</c>/<c>Bundling</c>/<c>Extracting</c>/<c>Consolidating</c>/<c>Applying</c>
/// job exists per <see cref="PageId"/>. <c>HolocronJobService.EnqueueOrGetActiveAsync</c>
/// enforces this atomically.
/// </summary>
public sealed class HolocronJob
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();

    /// <summary>The node being enhanced. Matches <c>kg.nodes._id</c> / Wookieepedia PageId.</summary>
    [BsonElement("pageId")]
    public int PageId { get; set; }

    /// <summary>Snapshot of the node's name at job-creation time so the jobs UI can render without a join.</summary>
    [BsonElement("nodeName")]
    public string NodeName { get; set; } = string.Empty;

    [BsonElement("status")]
    [BsonRepresentation(BsonType.String)]
    public HolocronJobStatus Status { get; set; } = HolocronJobStatus.Queued;

    /// <summary><c>"manual"</c> (Knowledge Graph button) / <c>"scheduled"</c> (daily Hangfire pass) / <c>"admin"</c>.</summary>
    [BsonElement("triggeredBy")]
    public string TriggeredBy { get; set; } = "manual";

    [BsonElement("agentVersion")]
    public string AgentVersion { get; set; } = string.Empty;

    [BsonElement("modelId")]
    public string ModelId { get; set; } = string.Empty;

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [BsonElement("startedAt")]
    [BsonIgnoreIfNull]
    public DateTime? StartedAt { get; set; }

    [BsonElement("completedAt")]
    [BsonIgnoreIfNull]
    public DateTime? CompletedAt { get; set; }

    // ── Discovery + Bundler stats ─────────────────────────────────────────

    /// <summary>Total backlink chunks Discovery found before any skip-if-unchanged filtering.</summary>
    [BsonElement("chunksDiscovered")]
    public int ChunksDiscovered { get; set; }

    /// <summary>Chunks new or content-changed since last run for this node — these get bundled + sent to the LLM.</summary>
    [BsonElement("chunksProcessedNew")]
    public int ChunksProcessedNew { get; set; }

    /// <summary>Chunks skipped because the per-(node, chunk) ledger had a matching content hash. Saves $$$ on re-runs.</summary>
    [BsonElement("chunksSkippedUnchanged")]
    public int ChunksSkippedUnchanged { get; set; }

    [BsonElement("totalBatches")]
    public int TotalBatches { get; set; }

    /// <summary>Live counter — bumped by the extractor after each batch completes. Drives the UI progress bar.</summary>
    [BsonElement("completedBatches")]
    public int CompletedBatches { get; set; }

    // ── Final apply summary ───────────────────────────────────────────────

    /// <summary>Raw proposal count from the LLM across all batches, before consolidation/dedup.</summary>
    [BsonElement("proposalsExtracted")]
    public int ProposalsExtracted { get; set; }

    /// <summary>Proposals that survived consolidation + pre-flight and were written to kg.enrichments / kg.edge_enrichments.</summary>
    [BsonElement("enrichmentsApplied")]
    public int EnrichmentsApplied { get; set; }

    /// <summary>Cross-batch duplicates merged by the consolidator (same field/edge proposed in multiple batches).</summary>
    [BsonElement("duplicatesDropped")]
    public int DuplicatesDropped { get; set; }

    /// <summary>Proposals that cited a non-existent <c>chunkId</c> or <c>sourcePageId</c>.</summary>
    [BsonElement("evidenceFailures")]
    public int EvidenceFailures { get; set; }

    /// <summary>Proposals dropped by pre-flight rules (canonical labels, no parallel pairs, etc.).</summary>
    [BsonElement("preflightRejects")]
    public int PreflightRejects { get; set; }

    /// <summary>
    /// Proposals dropped by the LLM evidence verifier (Design-025 follow-up). The
    /// verifier runs after the consolidator and asks the model whether each cited
    /// chunk excerpt directly supports the claim — catches target-substitution
    /// hallucinations like "claim says 'commander', target is 'Clone Captain'"
    /// that the structural pre-flight can't see. <c>0</c> for legacy rows / runs
    /// without the verifier stage.
    /// </summary>
    [BsonElement("verifierRejects")]
    [BsonIgnoreIfDefault]
    public int VerifierRejects { get; set; }

    [BsonElement("error")]
    [BsonIgnoreIfNull]
    public string? Error { get; set; }
}

/// <summary>
/// Lifecycle states of a <see cref="HolocronJob"/>. Forward-only — once a job hits a
/// terminal state (<see cref="Completed"/> or <see cref="Failed"/>) it does not transition
/// again. The intermediate states map 1:1 to the 5-executor pipeline so the UI can show
/// "what is the agent doing right now".
///
/// Serialized as the case-name string on the JSON wire (e.g. <c>"Completed"</c>) so
/// status responses are self-describing for the polling UI and external consumers.
/// Applied per-type rather than globally so other API enums keep their existing
/// integer-form serialization (the Frontend's HttpClient JSON options don't have a
/// matching converter for global string-form enums).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HolocronJobStatus
{
    /// <summary>Created via API; awaiting a Hangfire worker.</summary>
    Queued,

    /// <summary>HolocronContextDiscoveryExecutor running — pulling backlink chunks + applying skip-if-unchanged.</summary>
    Discovering,

    /// <summary>HolocronBundlerExecutor running — grouping chunks into batches.</summary>
    Bundling,

    /// <summary>HolocronProposalExtractorExecutor running — one LLM call per batch.</summary>
    Extracting,

    /// <summary>HolocronConsolidatorExecutor running — dedupe + pre-flight.</summary>
    Consolidating,

    /// <summary>HolocronEvidenceVerifierExecutor running — LLM second-pass verifying each surviving proposal's evidence.</summary>
    Verifying,

    /// <summary>HolocronApplyExecutor running — writes to kg.enrichments / kg.edge_enrichments / kg.events.</summary>
    Applying,

    /// <summary>Terminal — pipeline finished cleanly.</summary>
    Completed,

    /// <summary>Terminal — pipeline failed; <see cref="HolocronJob.Error"/> carries detail.</summary>
    Failed,
}
