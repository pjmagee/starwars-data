using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Services.KnowledgeGraph.Definitions;

namespace StarWarsData.Services;

/// <summary>
/// AI tools for KG-backed analytics — real MongoDB aggregation pipelines that return
/// chart-ready numeric data. The AI picks the right tool, then feeds results into render_chart.
/// </summary>
public class KGAnalyticsToolkit
{
    readonly KnowledgeGraphQueryService _kg;
    readonly IMongoCollection<RelationshipLabel> _labels;

    public KGAnalyticsToolkit(KnowledgeGraphQueryService kg, IMongoClient mongoClient, string databaseName)
    {
        _kg = kg;
        _labels = mongoClient.GetDatabase(databaseName).GetCollection<RelationshipLabel>(Collections.KgLabels);
    }

    [Description(
        "Count entities of one type connected to each entity of another type via a relationship label. "
            + "Returns real aggregated counts from the knowledge graph — use these for accurate chart data. "
            + "Example: 'Deadliest wars by battles' → entityType='War', relatedType='Battle', label='battle_in'. "
            + "Call list_relationship_labels and list_entity_types first to discover valid values. "
            + "Best for: Bar, Pie, Donut, Rose, StackedBar charts."
    )]
    public async Task<string> CountRelatedEntities(
        [Description("The entity type to group by (e.g. War, Organization, Planet)")] string entityType,
        [Description("The related entity type being counted (e.g. Battle, Character, Starship)")] string relatedType,
        [Description("The KG edge label connecting them (e.g. battle_in, member_of, fought_on)")] string label,
        [Description("'source' to group by the source entity, 'target' to group by the target entity.Use 'source' when entityType is the fromType in edges, 'target' when it's the toType.")]
            string groupBy = "source",
        [Description("Sort order: 'desc' for highest first, 'asc' for lowest first")] string sort = "desc",
        [Description("Max results (default 20, max 50)")] int limit = 20,
        [Description("Optional continuity filter: Canon, Legends, or omit for all")] string? continuity = null
    )
    {
        var results = await _kg.CountRelatedEntitiesAsync(entityType, relatedType, label, groupBySource: groupBy != "target", continuity, limit);

        if (sort == "asc")
            results.Reverse();

        return JsonSerializer.Serialize(
            results.Select(r => new
            {
                name = r.name,
                pageId = r.id,
                count = r.count,
            })
        );
    }

    [Description(
        "Count knowledge graph nodes grouped by a property value. "
            + "Example: 'Characters by species' → entityType='Character', property='Species'. "
            + "Property names are infobox field names (case-sensitive, typically PascalCase like 'Species', 'Homeworld', 'Manufacturer'). "
            + "Use get_entity_properties on a sample entity first to discover available property names. "
            + "Best for: Pie, Donut, Rose, Bar charts."
    )]
    public async Task<string> CountNodesByProperty(
        [Description("Entity type to aggregate (e.g. Character, Starship, CelestialBody)")] string entityType,
        [Description("Property name to group by (e.g. Species, Homeworld, Manufacturer). Case-sensitive.")] string property,
        [Description("Max results (default 20, max 50)")] int limit = 20,
        [Description("Optional continuity filter: Canon, Legends, or omit for all")] string? continuity = null
    )
    {
        var results = await _kg.CountNodesByPropertyAsync(entityType, property, continuity, limit);

        return JsonSerializer.Serialize(results.Select(r => new { value = r.value, count = r.count }));
    }

    [Description(
        """
                Count nodes or events bucketed by year ranges for temporal charts.
                Without 'semantic': counts by the entity's startYear (when it came into existence).
                With 'semantic': counts by a specific temporal facet dimension for precise lifecycle queries.
                Semantic dimensions: 'lifespan.start' (born), 'lifespan.end' (died),
                'conflict.start' (war/battle began), 'conflict.end' (war/battle ended),
                'institutional.start' (founded), 'institutional.end' (dissolved), 'institutional.reorganized',
                'construction.start' (built), 'construction.end' (destroyed),
                'creation.start' (created), 'publication.start' (released).
                Examples: 'Characters who died per year in Clone Wars' → entityType='Character', startYear=-22, endYear=-19, semantic='lifespan.end'.
                'Battles started per year' → entityType='Battle', semantic='conflict.start'.
                'Ships built per decade' → entityType='Starship', bucket=10, semantic='construction.start'.
                Years use sort-key format: negative = BBY, positive = ABY.
                Best for: TimeSeries, Line charts
            """
    )]
    public async Task<string> CountByYearRange(
        [Description("Entity type to count (e.g. Battle, Character, Starship)")] string entityType,
        [Description("Start year in sort-key format (-22 = 22 BBY, 4 = 4 ABY)")] int startYear,
        [Description("End year in sort-key format")] int endYear,
        [Description("Bucket size in years (1 = per year, 10 = per decade, 100 = per century)")] int bucket = 1,
        [Description("Optional continuity filter: Canon, Legends, or omit for all")] string? continuity = null,
        [Description(
            "Optional temporal facet semantic filter. Prefix-matches on temporalFacets.semantic. "
                + "E.g. 'lifespan.end' for deaths, 'conflict.start' for battle/war beginnings, "
                + "'institutional.reorganized' for government reorganizations. "
                + "Omit to use the flat startYear envelope."
        )]
            string? semantic = null
    )
    {
        var results = await _kg.CountByYearRangeAsync(entityType, startYear, endYear, bucket, continuity, semantic);

        return JsonSerializer.Serialize(
            results.Select(r => new
            {
                year = r.year,
                yearDisplay = r.year < 0 ? $"{Math.Abs(r.year)} BBY"
                : r.year == 0 ? "0 BBY/ABY"
                : $"{r.year} ABY",
                count = r.count,
            })
        );
    }

    [Description(
        "Count edges between two entity types, grouped by relationship label. "
            + "Use to discover HOW two types are connected before drilling deeper with count_related_entities. "
            + "Example: 'How are Wars and Characters connected?' → fromType='War', toType='Character'. "
            + "Best for: Bar, StackedBar charts."
    )]
    public async Task<string> CountEdgesBetweenTypes(
        [Description("Source entity type (e.g. War, Organization)")] string fromType,
        [Description("Target entity type (e.g. Character, Battle, Planet)")] string toType,
        [Description("Max results (default 20, max 50)")] int limit = 20,
        [Description("Optional continuity filter: Canon, Legends, or omit for all")] string? continuity = null
    )
    {
        var results = await _kg.CountEdgesBetweenTypesAsync(fromType, toType, continuity, limit);

        return JsonSerializer.Serialize(results.Select(r => new { label = r.label, count = r.count }));
    }

    [Description(
        "Rank entities by total number of relationships (edge degree). "
            + "Example: 'Most connected characters' → entityType='Character'. "
            + "'Most referenced planets in battles' → entityType='Planet', label='fought_on'. "
            + "Best for: Bar, Donut charts."
    )]
    public async Task<string> TopConnectedEntities(
        [Description("Optional entity type filter (e.g. Character, Planet). Omit for all types.")] string? entityType = null,
        [Description("Optional edge label filter (e.g. member_of, fought_on). Omit for all labels.")] string? label = null,
        [Description("Max results (default 20, max 50)")] int limit = 20,
        [Description("Optional continuity filter: Canon, Legends, or omit for all")] string? continuity = null
    )
    {
        var results = await _kg.TopConnectedEntitiesAsync(entityType, label, continuity, limit);

        return JsonSerializer.Serialize(
            results.Select(r => new
            {
                name = r.name,
                pageId = r.id,
                degree = r.degree,
            })
        );
    }

    [Description(
        "Get a multi-dimensional profile of a single entity by counting edges across multiple labels. "
            + "Returns one count per label — perfect for Radar charts comparing entities. "
            + "Example: Compare Clone Wars profile → entityId=<clone wars pageId>, labels=['battle_in','combatant_in','fought_on','fought_by']. "
            + "Call search_entities first to get the PageId, and list_relationship_labels to discover labels. "
            + "Best for: Radar charts. Also useful for StackedBar when comparing multiple entities."
    )]
    public async Task<string> EntityProfile(
        [Description("The PageId of the entity (from search_entities)")] int entityId,
        [Description("List of edge labels to count (each becomes a Radar axis or StackedBar dimension)")] List<string> labels,
        [Description("Optional continuity filter: Canon, Legends, or omit for all")] string? continuity = null
    )
    {
        var results = await _kg.EntityProfileAsync(entityId, labels, continuity);

        return JsonSerializer.Serialize(results.Select(r => new { dimension = r.dimension, count = r.count }));
    }

    [Description("Count knowledge graph nodes grouped by entity type. " + "Returns the distribution of entity types in the KG. " + "Best for: Pie, Donut, Bar charts showing overall KG composition.")]
    public async Task<string> CountNodesByType(
        [Description("Optional continuity filter: Canon, Legends, or omit for all")] string? continuity = null,
        [Description("Max results (default 30, max 50)")] int limit = 30
    )
    {
        var results = await _kg.CountNodesByTypeAsync(continuity, limit);

        return JsonSerializer.Serialize(results.Select(r => new { type = r.type, count = r.count }));
    }

    [Description(
        "For a specific named entity, get the property distribution of its connected entities. "
            + "Example: 'Species breakdown of Jedi Order members' → rootEntityId=<Jedi Order pageId>, "
            + "label='member_of', property='Species', rootIsTarget='true' (members point TO the Jedi Order). "
            + "'Homeworlds of Imperial officers' → rootEntityId=<Galactic Empire pageId>, label='member_of', property='Homeworld'. "
            + "Call search_entities first to get the rootEntityId. "
            + "Best for: Pie, Donut, Rose, Bar charts with named-entity context."
    )]
    public async Task<string> CountPropertyForRelatedEntities(
        [Description("PageId of the root entity (e.g. the Jedi Order, Galactic Empire). Use search_entities to find it.")] int rootEntityId,
        [Description("Edge label connecting entities to the root (e.g. 'member_of', 'trained_by', 'fought_in')")] string label,
        [Description("Property to group the connected entities by (e.g. 'Species', 'Homeworld', 'Gender'). Case-sensitive.")] string property,
        [Description(
            "Whether the root entity is the TARGET of the edges (true) or the SOURCE (false). "
                + "E.g. for 'member_of' edges, members are FROM and the org is TO — so rootIsTarget=true. "
                + "For 'trained' edges from a master, the master is FROM — so rootIsTarget=false."
        )]
            bool rootIsTarget = true,
        [Description("Max results (default 20, max 50)")] int limit = 20,
        [Description("Optional continuity filter: Canon, Legends, or omit for all")] string? continuity = null
    )
    {
        var results = await _kg.CountPropertyForRelatedEntitiesAsync(rootEntityId, label, property, rootIsTarget, continuity, limit);

        return JsonSerializer.Serialize(results.Select(r => new { value = r.value, count = r.count }));
    }

    [Description(
        "Group entities by what they connect to via a relationship label — returns named target entities as chart categories. "
            + "Example: 'Characters grouped by faction' → sourceType='Character', label='member_of' "
            + "→ [{name: 'Galactic Empire', count: 450}, {name: 'Jedi Order', count: 230}, ...]. "
            + "'Battles grouped by war' → sourceType='Battle', label='battle_in'. "
            + "'Planets grouped by governing faction' → sourceType='CelestialBody', label='governed_by'. "
            + "Best for: Bar, Pie, Donut charts where you want named factions/organizations/wars as labels."
    )]
    public async Task<string> GroupEntitiesByConnection(
        [Description("Type of the source entities being grouped (e.g. Character, Battle, CelestialBody)")] string sourceType,
        [Description("Edge label from source to target (e.g. 'member_of', 'battle_in', 'governed_by')")] string label,
        [Description("Optional target type filter (e.g. 'Organization', 'War'). Omit for all target types.")] string? targetType = null,
        [Description("Max groups to return (default 20, max 50)")] int limit = 20,
        [Description("Optional continuity filter: Canon, Legends, or omit for all")] string? continuity = null
    )
    {
        var results = await _kg.GroupEntitiesByConnectionAsync(sourceType, label, targetType, continuity, limit);

        return JsonSerializer.Serialize(
            results.Select(r => new
            {
                name = r.name,
                pageId = r.id,
                count = r.count,
            })
        );
    }

    [Description(
        "Compare multiple named entities side-by-side across relationship dimensions. "
            + "Returns one series per entity with counts for each label — perfect for Radar and StackedBar comparisons. "
            + "Example: 'Compare Yoda vs Palpatine vs Dooku' → entityIds=[yodaId, palpatineId, dookuId], "
            + "labels=['trained','trained_by','member_of','fought_in','allied_with']. "
            + "Call search_entities first to get PageIds, and list_relationship_labels to discover labels. "
            + "Best for: Radar (overlay profiles), StackedBar (side-by-side dimensions)."
    )]
    public async Task<string> CompareEntities(
        [Description("PageIds of the entities to compare (max 10). Use search_entities to find them.")] List<int> entityIds,
        [Description("Edge labels to measure for each entity (each becomes an axis/dimension)")] List<string> labels,
        [Description("Optional continuity filter: Canon, Legends, or omit for all")] string? continuity = null
    )
    {
        var results = await _kg.CompareEntitiesAsync(entityIds, labels, continuity);

        return JsonSerializer.Serialize(
            results.Select(r => new
            {
                entityId = r.entityId,
                name = r.entityName,
                dimensions = r.dimensions.Select(d => new { label = d.label, count = d.count }),
            })
        );
    }

    [Description(
        "Get detailed info about relationship labels including which entity types they connect, "
            + "their reverse labels, descriptions, and usage counts. "
            + "ESSENTIAL discovery tool: call this BEFORE analytics queries to understand which labels "
            + "connect which types and in which direction. "
            + "Example: describe_relationship_labels(labels=['member_of','trained_by','battle_in']) → "
            + "shows that 'member_of' goes from Character→Organization, 'battle_in' from Battle→War, etc. "
            + "Without arguments, returns the top labels by usage. "
            + "Use this to plan your analytics queries: it tells you the fromType, toType, and direction for each label."
    )]
    public async Task<string> DescribeRelationshipLabels(
        [Description("Optional list of specific label names to describe. " + "If omitted, returns the top labels by usage count.")] List<string>? labels = null,
        [Description("Max results when not filtering by specific labels (default 30)")] int limit = 30
    )
    {
        List<RelationshipLabel> results;

        if (labels is { Count: > 0 })
        {
            results = await _labels.Find(Builders<RelationshipLabel>.Filter.In(l => l.Label, labels)).ToListAsync();
        }
        else
        {
            results = await _labels.Find(FilterDefinition<RelationshipLabel>.Empty).SortByDescending(l => l.UsageCount).Limit(Math.Min(limit, 50)).ToListAsync();
        }

        return JsonSerializer.Serialize(
            results.Select(l => new
            {
                label = l.Label,
                reverse = l.Reverse,
                description = l.Description,
                fromTypes = l.FromTypes,
                toTypes = l.ToTypes,
                usageCount = l.UsageCount,
            })
        );
    }

    [Description(
        "Introspect the schema of an entity type — which infobox fields it has, classified as "
            + "properties/relationships/temporal facets, plus which labels are primary for that type. "
            + "Use this to understand what data is available for a type before writing queries. "
            + "Example: describe_entity_schema('Battle') shows that Battle has commanders1/2, side1/2, "
            + "unit1/2, Date (temporal), Outcome (property), etc."
    )]
    public string DescribeEntitySchema([Description("Entity type name, e.g. 'Battle', 'Character', 'Government'")] string type)
    {
        var def = InfoboxDefinitionRegistry.ForTemplate(type);
        var primary = DefaultLabelSelector.GetDefaults(type, def.Relationships.Values.Select(r => r.Label).Distinct()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return JsonSerializer.Serialize(
            new
            {
                type,
                properties = def.Properties.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
                temporalFields = def
                    .TemporalFields.Select(kv => new
                    {
                        field = kv.Key,
                        semantic = kv.Value.Semantic,
                        calendar = kv.Value.Calendar,
                    })
                    .OrderBy(x => x.field, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                relationships = def
                    .Relationships.Select(kv => new
                    {
                        field = kv.Key,
                        label = kv.Value.Label,
                        reverse = kv.Value.Reverse,
                        targets = kv.Value.ExpectedTargetTypes,
                        category = kv.Value.Category,
                        description = kv.Value.Description,
                        primary = primary.Contains(kv.Value.Label),
                    })
                    .OrderBy(x => x.field, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            }
        );
    }

    [Description(
        "Find entities that experienced a specific lifecycle transition within a year range, "
            + "using temporal facet semantics. "
            + "Example: 'Governments reorganized between 20 BBY and 4 BBY' → "
            + "semantic='institutional.reorganized', startYear=-20, endYear=-4. "
            + "Semantic dimensions and roles: "
            + "lifespan.start/end (born/died), "
            + "conflict.start/end/point (war began/ended/happened), "
            + "institutional.start/end/reorganized/restored/fragmented/suspended (government lifecycle), "
            + "construction.start/end/rebuilt (built/destroyed), "
            + "creation.start/end/discovered (device created/retired/discovered), "
            + "publication.release/start/end (media released/first-issue/last-issue), "
            + "usage.start/end (first/last employed). "
            + "Returns entities with the facet text so you can see the transition reason."
    )]
    public async Task<string> FindByLifecycleTransition(
        [Description("Exact semantic dimension.role (e.g. 'institutional.reorganized', 'lifespan.end', 'conflict.start')")] string semantic,
        [Description("Start year in sort-key format (-19 = 19 BBY, 4 = 4 ABY, or CE year for publication)")] int startYear,
        [Description("End year in sort-key format")] int endYear,
        [Description("Optional entity type filter (e.g. 'Government', 'Character'). Omit for all types.")] string? entityType = null,
        [Description("Max results (default 30, max 100)")] int limit = 30,
        [Description("Optional continuity filter: Canon, Legends, or omit for all")] string? continuity = null
    )
    {
        var results = await _kg.FindByLifecycleTransitionAsync(semantic, startYear, endYear, entityType, continuity, Math.Min(limit, 100));

        return JsonSerializer.Serialize(
            results.Select(r => new
            {
                pageId = r.pageId,
                name = r.name,
                type = r.type,
                transition = new
                {
                    semantic = r.semantic,
                    year = r.year,
                    text = r.text,
                    calendar = r.calendar,
                },
            })
        );
    }

    [Description(
        "Count lifecycle transitions per year bucket for time-series charts. "
            + "Example: 'Governments reorganized per decade' → semantic='institutional.reorganized', bucket=10. "
            + "'Characters who died per year during the Clone Wars' → semantic='lifespan.end', startYear=-22, endYear=-19, bucket=1. "
            + "More precise than count_by_year_range for lifecycle transitions because it directly "
            + "counts the semantic facets rather than relying on the node's startYear envelope. "
            + "Best for: TimeSeries, Line charts."
    )]
    public async Task<string> CountLifecycleTransitions(
        [Description("Semantic dimension.role, e.g. 'institutional.reorganized', 'lifespan.end', 'conflict.start'")] string semantic,
        [Description("Start year in sort-key format")] int startYear,
        [Description("End year in sort-key format")] int endYear,
        [Description("Bucket size in years (1 = per year, 10 = per decade, 100 = per century)")] int bucket = 1,
        [Description("Optional entity type filter")] string? entityType = null,
        [Description("Optional continuity filter: Canon, Legends, or omit for all")] string? continuity = null
    )
    {
        var results = await _kg.CountLifecycleTransitionsAsync(semantic, startYear, endYear, bucket, entityType, continuity);

        return JsonSerializer.Serialize(
            results.Select(r => new
            {
                year = r.year,
                yearDisplay = r.year < 0 ? $"{Math.Abs(r.year)} BBY"
                : r.year == 0 ? "0 BBY/ABY"
                : $"{r.year} ABY",
                count = r.count,
            })
        );
    }

    [Description(
        "List all edge labels that belong to a specific semantic category. "
            + "Categories are declared in FieldSemantics: family, mentorship, military, political, "
            + "location, astronomy, biological, cultural, religion, economic, publication, creator, "
            + "possession, usage, sports, music, food, temporal, sequence, composition, medical, honors. "
            + "Use this to discover related labels — e.g. list_labels_by_category('family') returns "
            + "child_of, parent_of, sibling_of, partner_of, has_relative, family, etc."
    )]
    public string ListLabelsByCategory([Description("Category name (e.g. 'family', 'military', 'publication')")] string category)
    {
        var labels = FieldSemantics
            .Relationships.Values.Where(d => string.Equals(d.Category, category, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(d => d.Label)
            .Select(d => new
            {
                label = d.Label,
                reverse = d.Reverse,
                description = d.Description,
                targets = d.ExpectedTargetTypes,
            })
            .OrderBy(x => x.label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return JsonSerializer.Serialize(
            new
            {
                category,
                count = labels.Count,
                labels,
            }
        );
    }

    [Description(
        "Get an entity's relationships narrowed to a single semantic category. Returns the connected "
            + "entities grouped by label, scoped to one topical lens. "
            + "Example: Yoda's family/mentorship relationships → category='mentorship'. "
            + "Anakin's military relationships → category='military'. "
            + "Palpatine's political relationships → category='political'. "
            + "Use this instead of get_entity_relationships when you only want one topic — "
            + "avoids noise from unrelated relationship categories."
    )]
    public async Task<string> GetRelationshipsByCategory(
        [Description("The PageId of the entity (from search_entities)")] int entityId,
        [Description("Semantic category: family, mentorship, military, political, location, publication, etc.")] string category,
        [Description("Optional continuity filter: Canon, Legends, or omit for all")] string? continuity = null,
        [Description("Max edges to return (default 50, max 200)")] int limit = 50
    )
    {
        // Collect all labels that belong to the category (both forward and reverse forms)
        var categoryLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in FieldSemantics.Relationships.Values)
        {
            if (string.Equals(d.Category, category, StringComparison.OrdinalIgnoreCase))
            {
                categoryLabels.Add(d.Label);
                if (!string.IsNullOrEmpty(d.Reverse))
                    categoryLabels.Add(d.Reverse);
            }
        }

        if (categoryLabels.Count == 0)
            return JsonSerializer.Serialize(
                new
                {
                    entityId,
                    category,
                    relationships = new { },
                    note = $"No labels registered under category '{category}'. Try list_labels_by_category to see valid categories.",
                }
            );

        var labelFilter = string.Join(",", categoryLabels);
        var edges = await _kg.GetAllEdgesForEntityAsync(entityId, labelFilter, continuity, Math.Min(limit, 200));

        if (edges.Count == 0)
            return JsonSerializer.Serialize(
                new
                {
                    entityId,
                    category,
                    relationships = new { },
                    totalEdges = 0,
                }
            );

        var grouped = edges
            .GroupBy(e => e.Label)
            .ToDictionary(
                g => g.Key,
                g =>
                    g.Select(e => new
                        {
                            pageId = e.ToId,
                            name = e.ToName,
                            type = e.ToType,
                            fromYear = e.FromYear,
                            toYear = e.ToYear,
                        })
                        .ToList()
            );

        return JsonSerializer.Serialize(
            new
            {
                entityId,
                entityName = edges[0].FromName,
                category,
                relationships = grouped,
                totalEdges = edges.Count,
            }
        );
    }

    public IReadOnlyList<AITool> AsAIFunctions() =>
        [
            AIFunctionFactory.Create(DescribeRelationshipLabels, "describe_relationship_labels"),
            AIFunctionFactory.Create(CountRelatedEntities, "count_related_entities"),
            AIFunctionFactory.Create(CountNodesByProperty, "count_nodes_by_property"),
            AIFunctionFactory.Create(CountByYearRange, "count_by_year_range"),
            AIFunctionFactory.Create(CountEdgesBetweenTypes, "count_edges_between_types"),
            AIFunctionFactory.Create(TopConnectedEntities, "top_connected_entities"),
            AIFunctionFactory.Create(EntityProfile, "entity_profile"),
            AIFunctionFactory.Create(CountNodesByType, "count_nodes_by_type"),
            AIFunctionFactory.Create(CountPropertyForRelatedEntities, "count_property_for_related_entities"),
            AIFunctionFactory.Create(GroupEntitiesByConnection, "group_entities_by_connection"),
            AIFunctionFactory.Create(CompareEntities, "compare_entities"),
            AIFunctionFactory.Create(DescribeEntitySchema, "describe_entity_schema"),
            AIFunctionFactory.Create(FindByLifecycleTransition, "find_by_lifecycle_transition"),
            AIFunctionFactory.Create(CountLifecycleTransitions, "count_lifecycle_transitions"),
            AIFunctionFactory.Create(ListLabelsByCategory, "list_labels_by_category"),
            AIFunctionFactory.Create(GetRelationshipsByCategory, "get_relationships_by_category"),
        ];
}
