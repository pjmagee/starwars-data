namespace StarWarsData.Services.KnowledgeGraph.Definitions;

/// <summary>
/// The template-scoped slice of <see cref="FieldSemantics"/> — contains only the
/// fields that a specific infobox template actually has, classified as properties,
/// relationships, or temporal facets.
///
/// Built by <see cref="InfoboxDefinitionRegistry"/> by intersecting
/// <see cref="TemplateFields.ByTemplate"/> (which fields the template has in MongoDB)
/// with <see cref="FieldSemantics"/> (what each field means).
/// </summary>
public sealed class InfoboxDefinition
{
    /// <summary>The infobox template type name (e.g. "Battle", "Character").</summary>
    public string TypeName { get; }

    /// <summary>Field labels to store as scalar properties on the GraphNode.</summary>
    public HashSet<string> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Field labels that produce edges. Keyed by the infobox field label,
    /// value is the edge metadata (label, target types, description, etc.).
    /// </summary>
    public Dictionary<string, LabelDefinition> Relationships { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Field labels that produce temporal facets on the GraphNode.
    /// Keyed by the infobox field label, value is the semantic + calendar hint.
    /// </summary>
    public Dictionary<string, TemporalFieldDefinition> TemporalFields { get; } = new(StringComparer.OrdinalIgnoreCase);

    public InfoboxDefinition(string typeName)
    {
        TypeName = typeName;
    }
}
