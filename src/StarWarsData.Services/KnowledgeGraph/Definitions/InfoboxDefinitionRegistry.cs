using System.Collections.Concurrent;

namespace StarWarsData.Services.KnowledgeGraph.Definitions;

/// <summary>
/// Per-template definition lookup. For each template (Battle, Character, Organization…)
/// produces an <see cref="InfoboxDefinition"/> containing ONLY the fields that template
/// actually has in the wiki data, classified using the global <see cref="FieldSemantics"/>.
///
/// <para>
/// Two data sources are combined at lookup time:
/// <list type="bullet">
///   <item><see cref="TemplateFields"/> — which fields each template actually has (auto-generated from MongoDB)</item>
///   <item><see cref="FieldSemantics"/> — what each field means (hand-curated semantic mappings)</item>
/// </list>
/// </para>
///
/// <para>
/// This solves the template-collision problem: if "Type" appears on both Character and
/// Starship with different meanings, each template's definition can still route it
/// through its own slice of the semantic lookup. It also ensures unknown-to-semantics
/// fields on a template fall through to the fallback link-extraction path instead of
/// being silently classified as something unrelated.
/// </para>
/// </summary>
public static class InfoboxDefinitionRegistry
{
    static readonly ConcurrentDictionary<string, InfoboxDefinition> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Fallback definition used for templates not present in <see cref="TemplateFields"/>
    /// (rare — usually because the template has fewer than 5 pages or hasn't been
    /// re-inventoried yet). Exposes the full global semantic dictionary so unknown
    /// pages still get sensible classification.
    /// </summary>
    public static readonly InfoboxDefinition Fallback = BuildFallback();

    /// <summary>
    /// Get the definition for a specific template type. Returns the fallback
    /// (full global semantics) for templates not in the whitelist.
    /// </summary>
    public static InfoboxDefinition ForTemplate(string? templateName)
    {
        if (string.IsNullOrEmpty(templateName))
            return Fallback;

        return _cache.GetOrAdd(templateName, BuildForTemplate);
    }

    /// <summary>
    /// Look up the edge metadata for a field on a specific template.
    /// Returns null if the field is not a relationship on that template.
    /// </summary>
    public static LabelDefinition? FindRelationship(string templateName, string fieldLabel)
    {
        var def = ForTemplate(templateName);
        return def.Relationships.TryGetValue(fieldLabel, out var rel) ? rel : null;
    }

    /// <summary>
    /// True if the given field is a scalar property on the given template.
    /// </summary>
    public static bool IsProperty(string templateName, string fieldLabel)
    {
        var def = ForTemplate(templateName);
        return def.Properties.Contains(fieldLabel);
    }

    /// <summary>
    /// Look up the temporal facet mapping for a field on a specific template.
    /// Returns null if the field is not a temporal field on that template.
    /// </summary>
    public static TemporalFieldDefinition? FindTemporal(string templateName, string fieldLabel)
    {
        var def = ForTemplate(templateName);
        return def.TemporalFields.TryGetValue(fieldLabel, out var temp) ? temp : null;
    }

    /// <summary>
    /// All distinct edge label definitions across the global semantics registry,
    /// paired with the canonical edge label. Used to populate <c>kg.labels</c>.
    /// </summary>
    public static IEnumerable<LabelDefinition> AllLabelDefinitions() => FieldSemantics.Relationships.Values.DistinctBy(d => d.Label);

    /// <summary>
    /// True if any registered edge label with this canonical name targets a
    /// Character/Person entity. Replaces the old hardcoded IsPersonRelationshipLabel list.
    /// </summary>
    public static bool EdgeTargetsPerson(string edgeLabel)
    {
        foreach (var def in FieldSemantics.Relationships.Values)
        {
            if (def.Label == edgeLabel && def.TargetsPerson)
                return true;
        }
        return false;
    }

    static InfoboxDefinition BuildForTemplate(string templateName)
    {
        var def = new InfoboxDefinition(templateName);

        if (!TemplateFields.ByTemplate.TryGetValue(templateName, out var fields))
        {
            // Unknown template — return an empty scoped definition. Callers will fall
            // back to link-extraction for any field with links; no stale semantics leak in.
            return def;
        }

        // Intersect the template's actual fields with the global semantic dictionary.
        foreach (var field in fields)
        {
            if (FieldSemantics.Properties.Contains(field))
                def.Properties.Add(field);
            if (FieldSemantics.Relationships.TryGetValue(field, out var rel))
                def.Relationships[field] = rel;
            if (FieldSemantics.TemporalFields.TryGetValue(field, out var temp))
                def.TemporalFields[field] = temp;
        }

        return def;
    }

    static InfoboxDefinition BuildFallback()
    {
        var def = new InfoboxDefinition("_fallback");
        foreach (var p in FieldSemantics.Properties)
            def.Properties.Add(p);
        foreach (var (k, v) in FieldSemantics.Relationships)
            def.Relationships[k] = v;
        foreach (var (k, v) in FieldSemantics.TemporalFields)
            def.TemporalFields[k] = v;
        return def;
    }
}
