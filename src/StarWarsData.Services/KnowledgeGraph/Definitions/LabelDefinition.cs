namespace StarWarsData.Services.KnowledgeGraph.Definitions;

/// <summary>
/// Metadata describing a knowledge-graph edge label.
/// Drives the infobox classifier, the edge extractor, the kg.labels registry,
/// and downstream filters that need to know the expected target type of a label.
/// </summary>
public sealed record LabelDefinition(
    /// <summary>Canonical label used on the outgoing edge (snake_case).</summary>
    string Label,
    /// <summary>Reverse label used when walking edges in the opposite direction.</summary>
    string Reverse,
    /// <summary>Human-readable description shown in tool discovery.</summary>
    string Description,
    /// <summary>Entity types that typically appear as the TARGET of this edge.</summary>
    string[] ExpectedTargetTypes,
    /// <summary>Semantic grouping: family, mentorship, military, political, publication, location, etc.</summary>
    string Category,
    /// <summary>Default confidence weight for deterministically-extracted edges.</summary>
    double Weight = 1.0
)
{
    /// <summary>
    /// True if this edge is expected to target a Character/Person entity.
    /// Replaces the old hardcoded IsPersonRelationshipLabel list.
    /// </summary>
    public bool TargetsPerson => ExpectedTargetTypes.Any(static t => t is "Character" or "Person");
}
