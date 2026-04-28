namespace StarWarsData.Models.Queries;

/// <summary>
/// Overall progress summary for the relationship graph builder dashboard.
/// </summary>
public class GraphBuilderProgress
{
    public int TotalPages { get; init; }
    public int ProcessedPages { get; init; }
    public int SkippedPages { get; init; }
    public int FailedPages { get; init; }
    public int PendingPages { get; init; }
    public long TotalEdges { get; init; }
    public int TotalLabels { get; init; }

    /// <summary>Pages processed per hour over the last hour.</summary>
    public double PagesPerHour { get; init; }

    /// <summary>Estimated hours remaining at current throughput.</summary>
    public double? EstimatedHoursRemaining { get; init; }

    public List<TypeProgress> ByType { get; init; } = [];
    public List<RecentLabel> RecentLabels { get; init; } = [];
}

public class TypeProgress
{
    public string Type { get; init; } = string.Empty;
    public int Total { get; init; }
    public int Processed { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }
}

public class RecentLabel
{
    public string Label { get; init; } = string.Empty;
    public string Reverse { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int UsageCount { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// Result of a $graphLookup traversal from the persistent relationship graph.
/// </summary>
public class RelationshipGraphResult
{
    public int RootId { get; init; }
    public string RootName { get; init; } = string.Empty;
    public List<RelationshipGraphNode> Nodes { get; init; } = [];
    public List<RelationshipGraphEdge> Edges { get; init; } = [];
    public bool Truncated { get; init; }
}

public class RelationshipGraphNode
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string ImageUrl { get; init; } = string.Empty;
}

public class RelationshipGraphEdge
{
    public int FromId { get; init; }
    public int ToId { get; init; }
    public string Label { get; init; } = string.Empty;
    public double Weight { get; init; }
    public int? FromYear { get; init; }
    public int? ToYear { get; init; }
}

/// <summary>
/// The relationship labels available on a node plus a pre-computed set of
/// "default enabled" labels (the subset the UI should turn on initially for
/// that node's entity type to avoid noisy graphs).
/// </summary>
/// <summary>
/// One edge touching a single node, projected from the node's point of view (incoming
/// edges have already been mapped to their forward equivalent via FieldSemantics, so
/// the caller doesn't need to reason about direction). Carries Phase 2 annotation
/// context inline when present — see Design-019. Used by the Knowledge Graph node-detail
/// panel's relationships table.
/// </summary>
public class EntityEdgeRowDto
{
    public string Label { get; init; } = string.Empty;

    /// <summary>"out" when the source is the queried node, "in" otherwise.</summary>
    public string Direction { get; init; } = "out";

    /// <summary>The OTHER entity in the relationship (not the queried node).</summary>
    public int OtherId { get; init; }

    public string OtherName { get; init; } = string.Empty;
    public string OtherType { get; init; } = string.Empty;

    /// <summary>Edge temporal bounds, surfaced for the years column.</summary>
    public int? FromYear { get; init; }
    public int? ToYear { get; init; }

    /// <summary>Phase 1 edge metadata qualifier (e.g. "informal apprentice"). Carried separately from Phase 2 annotations.</summary>
    public string? Phase1Qualifier { get; init; }

    // ── Phase 2 (Holocron) annotation fields — null when no enrichment touches this edge ──

    /// <summary>True when the edge has NO Phase 1 record — created by a Holocron Add enrichment.</summary>
    public bool IsHolocronOnly { get; init; }

    /// <summary>Holocron Annotate role (e.g. "Jedi General"). Null when no Annotate enrichment exists.</summary>
    public string? Role { get; init; }

    /// <summary>Holocron Annotate qualifier (e.g. "during the Clone Wars").</summary>
    public string? Qualifier { get; init; }

    /// <summary>Holocron Annotate longer-form description.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// When the most recent Holocron enrichment touched this edge (Apply step's write
    /// timestamp). Null when no Holocron enrichment exists. The relationships table
    /// renders this as a relative "X ago" caption alongside the role/qualifier.
    /// </summary>
    public DateTime? HolocronAppliedAt { get; init; }

    /// <summary>
    /// Job that produced the most recent Holocron enrichment on this edge. Empty for
    /// pre-Phase-B rows from the legacy synchronous path. Used to deep-link the row
    /// to <c>/holocron/jobs/{pageId}</c> so users can pivot from "this edge has a
    /// Holocron note" to "show me the run that produced it".
    /// </summary>
    public string? HolocronJobId { get; init; }

    /// <summary>Agent version stamp on the most recent Holocron enrichment (e.g. <c>holocron-v1.0.0</c>).</summary>
    public string? HolocronAgentVersion { get; init; }
}

/// <summary>Top-level response for <c>GET /api/RelationshipGraph/edges/{nodeId}</c>.</summary>
public class EntityEdgesResult
{
    public int NodeId { get; init; }
    public string NodeName { get; init; } = string.Empty;

    /// <summary>All distinct edges (deduped per direction) up to the per-call cap, sorted by Holocron-richness then weight.</summary>
    public List<EntityEdgeRowDto> Edges { get; init; } = [];

    /// <summary>Total edges that touch this node before truncation. UI shows "showing X of Y" when greater.</summary>
    public int Total { get; init; }
}

/// <summary>
/// Phase 2 attribute enrichment for a single node — the claim, the proposed values, the
/// agent's reasoning, and the verbatim evidence excerpts. Powers the dedicated
/// "Holocron-added attributes" panel on the Knowledge Graph node-detail panel; Phase 1
/// attributes (the infobox-derived ones) render separately and are not mixed in.
/// Mirrors the per-enrichment shape on the <c>/holocron</c> log detail page so the two
/// surfaces tell the same provenance story.
/// </summary>
public class EntityNodeEnrichmentRowDto
{
    public string Id { get; init; } = string.Empty;
    public string FieldPath { get; init; } = string.Empty;

    /// <summary>"Add" or "Augment" — node enrichments are property-only in v1; FillGap/Annotate are edge-only.</summary>
    public string Operation { get; init; } = string.Empty;

    /// <summary>The agent-proposed values. Single-element list for scalar properties; multiple for list-valued ones.</summary>
    public List<string> Values { get; init; } = [];

    /// <summary>One-sentence statement of what the agent is asserting about the node.</summary>
    public string Claim { get; init; } = string.Empty;

    /// <summary>Free-form summary of the agent's reasoning. Optional.</summary>
    public string? LlmReasoning { get; init; }

    public List<EntityEnrichmentEvidenceDto> Evidence { get; init; } = [];

    /// <summary><c>holocron-vX.Y.Z</c> stamp from the agent that produced this enrichment.</summary>
    public string AgentVersion { get; init; } = string.Empty;
    public string ModelId { get; init; } = string.Empty;

    /// <summary>When the agent applied this enrichment. Surfaced as a relative timestamp in the panel.</summary>
    public DateTime? AppliedAt { get; init; }
}

public class EntityEnrichmentEvidenceDto
{
    /// <summary>The KG node whose article supplied the excerpt. Null for chunks not tied to a specific node.</summary>
    public int? SourcePageId { get; init; }

    /// <summary>Resolved name for <see cref="SourcePageId"/>. Null when the page is unknown or deleted.</summary>
    public string? SourcePageName { get; init; }

    /// <summary>The article-chunk id (search.chunks._id) that the agent cited. Null when only the page was cited.</summary>
    public string? ChunkId { get; init; }

    /// <summary>Verbatim excerpt the agent attached as evidence. Truncated server-side to 1KB.</summary>
    public string Excerpt { get; init; } = string.Empty;

    public double? RelevanceScore { get; init; }
}

/// <summary>Top-level response for <c>GET /api/RelationshipGraph/node-enrichments/{pageId}</c>.</summary>
public class EntityNodeEnrichmentsResult
{
    public int NodeId { get; init; }
    public string NodeName { get; init; } = string.Empty;

    /// <summary>All Active hash-matched node enrichments for this entity, newest first.</summary>
    public List<EntityNodeEnrichmentRowDto> Enrichments { get; init; } = [];
}

public class EntityLabelsResult
{
    /// <summary>Entity type (Character, Battle, Organization, ...). Empty if unknown.</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>All distinct relationship labels for the node (outgoing + reversed inbound).</summary>
    public List<string> Labels { get; init; } = [];

    /// <summary>
    /// Subset of <see cref="Labels"/> that should be enabled by default when rendering
    /// a graph for this node — typically the labels whose semantic category is
    /// prioritized for the node's entity type.
    /// </summary>
    public List<string> DefaultEnabled { get; init; } = [];

    /// <summary>
    /// Subset of <see cref="Labels"/> where every edge with that label between this node
    /// and any neighbour came from a Holocron <c>Add</c> enrichment — there is NO base
    /// <c>kg.edges</c> entry with this label. The UI renders these chips in
    /// <c>Color.Secondary</c> to mark them as Phase 2-only. See Design-019.
    /// </summary>
    public List<string> HolocronOnlyLabels { get; init; } = [];

    /// <summary>
    /// Subset of <see cref="Labels"/> where at least one edge has a Holocron <c>Annotate</c>
    /// or <c>FillGap</c> enrichment attached. The UI keeps the chip's primary colour but
    /// adds a small secondary-colour dot to signal "Phase 1 with Phase 2 overlay".
    /// </summary>
    public List<string> HolocronAnnotatedLabels { get; init; } = [];
}

/// <summary>
/// Paginated browse result for processed entities in the relationship graph.
/// </summary>
public class BrowseEntitiesResult
{
    public List<EntitySearchDto> Items { get; init; } = [];
    public long Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
}

/// <summary>
/// Progress summary for the article chunking dashboard.
/// </summary>
/// <summary>
/// Summary of an OpenAI Batch API job for the dashboard.
/// </summary>
public class GraphBatchSummary
{
    public string Id { get; init; } = string.Empty;
    public string OpenAiBatchId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public int TotalRequests { get; init; }
    public int CompletedRequests { get; init; }
    public int FailedRequests { get; init; }
    public int SkippedRequests { get; init; }
    public int TotalEdgesStored { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public DateTime? ProcessedAt { get; init; }
    public string? Error { get; init; }
}

public class ChunkingProgress
{
    public int TotalEligiblePages { get; init; }
    public int ChunkedPages { get; init; }
    public int PendingPages { get; init; }
    public long TotalChunks { get; init; }
    public double AvgChunksPerPage { get; init; }
    public double PagesPerHour { get; init; }
    public double? EstimatedHoursRemaining { get; init; }
    public List<ChunkingTypeProgress> ByType { get; init; } = [];
}

public class ChunkingTypeProgress
{
    public string Type { get; init; } = string.Empty;
    public int Pages { get; init; }
    public long Chunks { get; init; }
    public double AvgChunksPerPage { get; init; }
}

/// <summary>
/// Aggregated stats for a single relationship label across kg.edges.
/// One row = one directed label. Counts are directional (not pair-counts).
/// </summary>
public class EdgeLabelStatsDto
{
    public string Label { get; init; } = string.Empty;
    public long Count { get; init; }
    public long CanonCount { get; init; }
    public long LegendsCount { get; init; }
    public double AvgWeight { get; init; }

    /// <summary>Top source entity types with counts (max 5).</summary>
    public List<TypeCount> TopFromTypes { get; init; } = [];

    /// <summary>Top target entity types with counts (max 5).</summary>
    public List<TypeCount> TopToTypes { get; init; } = [];

    /// <summary>One example edge for context.</summary>
    public EdgeSample? Sample { get; init; }
}

public class TypeCount
{
    public string Type { get; init; } = string.Empty;
    public long Count { get; init; }
}

public class EdgeSample
{
    public int FromId { get; init; }
    public string FromName { get; init; } = string.Empty;
    public int ToId { get; init; }
    public string ToName { get; init; } = string.Empty;
}

public class BrowseEdgeLabelsResult
{
    public List<EdgeLabelStatsDto> Items { get; init; } = [];
    public long Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
}
