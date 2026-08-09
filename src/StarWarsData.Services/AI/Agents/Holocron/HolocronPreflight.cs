using MongoDB.Bson;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services.AI.Agents.Holocron;

/// <summary>
/// Shared pre-flight validators and enrichment-document factories used by both
/// Holocron paths: the v1 sync <c>HolocronAgent.ApplyProposalsAsync</c> and the
/// Design-020 async pipeline (<c>HolocronConsolidatorExecutor</c> +
/// <c>HolocronApplyExecutor</c>). Keeping one implementation eliminates the
/// historical "must mirror" contracts that drifted between the two copies.
/// </summary>
public static class HolocronPreflight
{
    /// <summary>
    /// True when the targeted bound on <paramref name="edge"/> is either null or
    /// explicitly tagged <see cref="EdgeBoundsSource.Lifecycle"/>. Untagged
    /// (<see cref="EdgeBoundsSource.Unknown"/>) bounds with a non-null value are
    /// treated as hard (Design-021).
    /// </summary>
    public static bool IsBoundRefinable(RelationshipEdge edge, bool isFrom)
    {
        var existing = isFrom ? edge.FromYear : edge.ToYear;
        if (!existing.HasValue)
            return true;
        var src = edge.Meta?.BoundsSource ?? EdgeBoundsSource.Unknown;
        return src is EdgeBoundsSource.Lifecycle;
    }

    /// <summary>
    /// Direction-agnostic node-pair key: <c>min(a,b)-max(a,b)</c>. Used by Add-edge
    /// pre-flight to reject any edge between an already-connected pair.
    /// </summary>
    public static string NodePairKey(int a, int b) => a < b ? $"{a}-{b}" : $"{b}-{a}";

    public static string EdgeKey(int from, int to, string label) => $"{from}-{to}-{label.ToLowerInvariant()}";

    /// <summary>
    /// At least one evidence item must resolve to a real source page or chunk.
    /// Accepts a projected <c>(sourcePageId, chunkId)</c> sequence so both the
    /// agent-path evidence records and <see cref="HolocronEvidencePayload"/> can
    /// share the same kernel.
    /// </summary>
    public static bool HasValidEvidence(IEnumerable<(int? SourcePageId, string? ChunkId)>? evidence, HashSet<int> validPageIds, HashSet<string> validChunkIds)
    {
        if (evidence is null)
            return false;

        foreach (var ev in evidence)
        {
            if (ev.SourcePageId is { } pid && pid > 0 && validPageIds.Contains(pid))
                return true;
            if (!string.IsNullOrEmpty(ev.ChunkId) && validChunkIds.Contains(ev.ChunkId))
                return true;
        }
        return false;
    }

    public static bool IsAnnotateValid(
        int focalPageId,
        int fromId,
        int toId,
        string? label,
        string? role,
        string? qualifier,
        string? description,
        HashSet<string> existingEdgeKeys
    )
    {
        if (fromId <= 0 || toId <= 0 || string.IsNullOrWhiteSpace(label))
            return false;
        if (fromId != focalPageId && toId != focalPageId)
            return false;
        if (!existingEdgeKeys.Contains(EdgeKey(fromId, toId, label)))
            return false;
        return !string.IsNullOrWhiteSpace(role) || !string.IsNullOrWhiteSpace(qualifier) || !string.IsNullOrWhiteSpace(description);
    }

    public static bool IsFillGapValid(
        int focalPageId,
        int fromId,
        int toId,
        string? label,
        int? fromYear,
        int? toYear,
        IEnumerable<RelationshipEdge> existingEdges
    )
    {
        if (fromId <= 0 || toId <= 0 || string.IsNullOrWhiteSpace(label))
            return false;
        if (fromId != focalPageId && toId != focalPageId)
            return false;
        if (!fromYear.HasValue && !toYear.HasValue)
            return false;

        // At least one proposed bound must be applicable: filling a NULL bound, OR
        // refining a Lifecycle-sourced bound (Design-021). Infobox / Unknown = hard.
        return existingEdges.Any(e =>
            e.FromId == fromId
            && e.ToId == toId
            && string.Equals(e.Label, label, StringComparison.OrdinalIgnoreCase)
            && ((fromYear.HasValue && IsBoundRefinable(e, isFrom: true)) || (toYear.HasValue && IsBoundRefinable(e, isFrom: false)))
        );
    }

    /// <summary>
    /// Add-edge pre-flight. Optional <paramref name="targetTypes"/> /
    /// <paramref name="expectedTargetsByLabel"/> enable the consolidator's
    /// type-constraint check; the agent path omits them.
    /// </summary>
    public static bool IsAddEdgeValid(
        int focalPageId,
        int fromId,
        int toId,
        string? label,
        HashSet<string> knownLabels,
        HashSet<string> existingNodePairs,
        HashSet<string> enrichmentNodePairs,
        IReadOnlyDictionary<int, string>? targetTypes = null,
        IReadOnlyDictionary<string, HashSet<string>>? expectedTargetsByLabel = null
    )
    {
        if (fromId <= 0 || toId <= 0 || string.IsNullOrWhiteSpace(label))
            return false;
        if (fromId != focalPageId && toId != focalPageId)
            return false;
        if (!knownLabels.Contains(label))
            return false;

        if (expectedTargetsByLabel is not null
            && expectedTargetsByLabel.TryGetValue(label, out var expected)
            && expected.Count > 0)
        {
            var targetType = targetTypes?.GetValueOrDefault(toId, string.Empty) ?? string.Empty;
            if (string.IsNullOrEmpty(targetType) || !expected.Contains(targetType))
                return false;
        }

        var pair = NodePairKey(fromId, toId);
        return !existingNodePairs.Contains(pair) && !enrichmentNodePairs.Contains(pair);
    }

    /// <summary>
    /// Add-vs-Augment kernel for node proposals. Returns <c>null</c> when every
    /// proposed value is blank or already present (silent skip).
    /// </summary>
    public static (EnrichmentOperation Op, List<string> FinalValues)? DecideNodeOp(IEnumerable<string>? values, IReadOnlyList<string>? existingProperties)
    {
        if (values is null)
            return null;

        var hasExisting = existingProperties is { Count: > 0 };
        if (hasExisting)
        {
            var finalValues = values.Where(v => !string.IsNullOrWhiteSpace(v) && !existingProperties!.Contains(v, StringComparer.OrdinalIgnoreCase)).ToList();
            if (finalValues.Count == 0)
                return null;
            return (EnrichmentOperation.Augment, finalValues);
        }

        var addValues = values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        if (addValues.Count == 0)
            return null;
        return (EnrichmentOperation.Add, addValues);
    }

    public static EdgeEnrichment BuildEdgeEnrichment(
        int fromId,
        int toId,
        string label,
        EnrichmentOperation op,
        BsonDocument value,
        string claim,
        List<EnrichmentEvidence> evidence,
        string reasoning,
        string fromHash,
        string toHash,
        string agentVersion,
        string modelId,
        string? jobId = null
    ) =>
        new()
        {
            FromId = fromId,
            ToId = toId,
            Label = label,
            Operation = op,
            Value = value,
            Claim = claim,
            Evidence = evidence,
            LlmReasoning = reasoning,
            ContentHashAtCreation = $"{fromHash}|{toHash}",
            Status = EnrichmentStatus.Active,
            AppliedAt = DateTime.UtcNow,
            AgentVersion = agentVersion,
            ModelId = modelId,
            JobId = jobId,
        };

    public static HolocronEvent BuildEdgeEvent(
        EdgeEnrichment doc,
        EnrichmentOperation op,
        string agentVersion,
        string triggeredBy,
        string? jobId = null
    ) =>
        new()
        {
            EventType = HolocronEventType.EdgeEnrichmentCreated,
            EnrichmentId = doc.Id,
            FromId = doc.FromId,
            ToId = doc.ToId,
            Label = doc.Label,
            Summary = $"Holocron {op.ToString().ToLowerInvariant()} edge `{doc.Label}` from {doc.FromId} to {doc.ToId}: {Truncate(doc.Claim, 200)}",
            TriggeredBy = triggeredBy,
            AgentVersion = agentVersion,
            JobId = jobId,
        };

    public static EnrichmentEvidence BuildEvidence(int? sourcePageId, string? chunkId, string? excerpt, double? relevanceScore) =>
        new()
        {
            SourcePageId = sourcePageId ?? 0,
            ChunkId = chunkId,
            Excerpt = string.IsNullOrEmpty(excerpt) ? string.Empty : (excerpt.Length > 1000 ? excerpt[..1000] : excerpt),
            RelevanceScore = relevanceScore,
        };

    public static BsonValue ToBsonValue(List<string> values) => values.Count == 1 ? new BsonString(values[0]) : new BsonArray(values);

    public static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? string.Empty
        : s.Length > max ? s[..max] + "…"
        : s;
}
