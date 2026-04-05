namespace StarWarsData.Services.KnowledgeGraph.Definitions;

/// <summary>
/// Selects a sensible subset of relationship labels to enable by default when
/// rendering a graph for a given entity type. Without this, a node like a
/// Character with 10+ available labels creates an overwhelming graph by
/// default (affiliated_with alone can pull in dozens of organizations).
///
/// Per-type prioritization uses the semantic <see cref="LabelDefinition.Category"/>
/// already declared in <see cref="FieldSemantics"/>. For each entity type we list
/// which categories are "primary" — labels whose category is in that list get
/// enabled by default; everything else is still available in the label chips,
/// just toggled off initially.
///
/// Unknown types fall through to "enable everything" so we don't accidentally
/// hide data for rare templates.
/// </summary>
public static class DefaultLabelSelector
{
    static readonly Dictionary<string, HashSet<string>> PrimaryCategoriesByType = new(StringComparer.OrdinalIgnoreCase)
    {
        // Character/Person — family, training, command, biological
        ["Character"] = ["family", "mentorship", "biological", "military"],
        ["Person"] = ["family", "publication", "political"],

        // Conflicts — military only (avoid has_conflict era-level pollution)
        ["Battle"] = ["military"],
        ["War"] = ["military"],
        ["Mission"] = ["military"],
        ["Duel"] = ["military"],
        ["Campaign"] = ["military"],
        ["Event"] = ["military", "political"],

        // Organizations — leadership and structure
        ["Organization"] = ["political", "military"],
        ["Government"] = ["political", "military"],
        ["Military_unit"] = ["military"],
        ["Fleet"] = ["military"],
        ["Company"] = ["economic", "political"],
        ["Religion"] = ["religion", "political"],

        // Locations — geography + astronomy, skip "has_conflict" unless explicitly toggled
        ["CelestialBody"] = ["astronomy", "location"],
        ["System"] = ["astronomy", "location"],
        ["Sector"] = ["location"],
        ["Region"] = ["location", "astronomy"],
        ["Nebula"] = ["astronomy", "location"],
        ["StarCluster"] = ["astronomy", "location"],
        ["Star"] = ["astronomy"],
        ["Structure"] = ["location", "military"],
        ["City"] = ["location", "political"],
        ["Location"] = ["location"],
        ["SpaceStation"] = ["location", "military", "technology"],
        ["TradeRoute"] = ["location", "economic"],

        // Technology / constructed objects
        ["IndividualShip"] = ["technology", "military", "creator"],
        ["StarshipClass"] = ["technology", "creator"],
        ["Weapon"] = ["technology", "creator"],
        ["Device"] = ["technology", "creator"],
        ["Droid"] = ["biological", "technology"],
        ["DroidSeries"] = ["technology", "creator"],
        ["Artifact"] = ["technology", "creator", "religion"],
        ["Lightsaber"] = ["technology", "creator"],
        ["Armor"] = ["technology", "creator"],
        ["Clothing"] = ["technology", "creator", "cultural"],

        // Publication / media
        ["Book"] = ["publication"],
        ["BookSeries"] = ["publication"],
        ["ComicBook"] = ["publication"],
        ["ComicSeries"] = ["publication"],
        ["ComicStory"] = ["publication"],
        ["ComicCollection"] = ["publication"],
        ["Movie"] = ["publication"],
        ["TelevisionEpisode"] = ["publication"],
        ["TelevisionSeries"] = ["publication"],
        ["VideoGame"] = ["publication"],
        ["Audiobook"] = ["publication"],
        ["Music"] = ["publication", "music"],
        ["MagazineArticle"] = ["publication"],
        ["MagazineIssue"] = ["publication"],

        // Biological
        ["Species"] = ["biological", "cultural", "location"],
        ["Family"] = ["family"],
        ["Plant"] = ["biological", "location"],
        ["Deity"] = ["religion"],

        // Misc
        ["Food"] = ["food", "cultural"],
        ["Language"] = ["cultural"],
        ["ForcePower"] = ["political"],
        ["Era"] = ["temporal"],
        ["Year"] = ["temporal"],
        ["Holiday"] = ["cultural", "political"],
        ["Medal"] = ["honors"],
        ["TitleOrPosition"] = ["political"],
        ["Election"] = ["political"],
    };

    /// <summary>
    /// Return the subset of <paramref name="availableLabels"/> that should be
    /// enabled by default for a node of <paramref name="entityType"/>.
    /// Falls back to the full list if the type has no priority config or if
    /// none of the available labels match the priority categories.
    /// </summary>
    public static List<string> GetDefaults(string entityType, IEnumerable<string> availableLabels)
    {
        var available = availableLabels?.ToList() ?? [];
        if (available.Count == 0)
            return [];

        if (!PrimaryCategoriesByType.TryGetValue(entityType ?? string.Empty, out var primaryCats))
        {
            // Unknown type — don't hide data.
            return available;
        }

        // Build label → category map from FieldSemantics. Both forward and reverse
        // labels inherit their definition's category so inbound relationships are
        // matched too (e.g. "commanded" reverse of "commanded_by" keeps the "military"
        // category).
        var labelCategories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in FieldSemantics.Relationships.Values)
        {
            labelCategories[def.Label] = def.Category;
            if (!string.IsNullOrEmpty(def.Reverse))
                labelCategories[def.Reverse] = def.Category;
        }

        var defaults = new List<string>();
        foreach (var label in available)
        {
            if (labelCategories.TryGetValue(label, out var cat) && primaryCats.Contains(cat))
                defaults.Add(label);
        }

        // If nothing matched, don't leave the graph empty — fall back to all.
        return defaults.Count > 0 ? defaults : available;
    }
}
