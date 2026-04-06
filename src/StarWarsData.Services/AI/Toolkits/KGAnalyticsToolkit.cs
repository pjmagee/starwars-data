using System.ComponentModel;
using Microsoft.Extensions.AI;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Services.KnowledgeGraph.Definitions;

namespace StarWarsData.Services;

/// <summary>
/// AI tools for KG-backed analytics — real MongoDB aggregation pipelines that return
/// chart-ready numeric data.
///
/// The agent picks a tool to aggregate KG data, then feeds the results into
/// <c>render_chart</c>/<c>render_data_table</c> from <see cref="ComponentToolkit"/>.
/// Every result comes from the knowledge graph rather than model estimation, so
/// counts are grounded in real documents.
///
/// Discovery tools (<c>describe_relationship_labels</c>, <c>describe_entity_schema</c>,
/// <c>list_labels_by_category</c>) should be called first to learn valid label names,
/// entity types, and directions before running aggregations.
/// </summary>
public class KGAnalyticsToolkit
{
    readonly KnowledgeGraphQueryService _kg;
    readonly IMongoCollection<RelationshipLabel> _labels;

    const string ContinuityParamDescription = "Optional continuity filter: Canon, Legends, or omit for all";

    public KGAnalyticsToolkit(KnowledgeGraphQueryService kg, IMongoClient mongoClient, string databaseName)
    {
        _kg = kg;
        _labels = mongoClient.GetDatabase(databaseName).GetCollection<RelationshipLabel>(Collections.KgLabels);
    }

    static string YearDisplay(int year) =>
        year < 0 ? $"{Math.Abs(year)} BBY"
        : year == 0 ? "0 BBY/ABY"
        : $"{year} ABY";

    [Description(
        """
            Count entities of one type connected to each entity of another type via a relationship label.
            Example: 'Deadliest wars by battles' → entityType='War', relatedType='Battle', label='battle_in'.
            Call list_relationship_labels and list_entity_types first to discover valid values.
            Best for: Bar, Pie, Donut, Rose, StackedBar charts.
            """
    )]
    public async Task<List<NamedCountDto>> CountRelatedEntities(
        [Description("Entity type to group by (e.g. War, Organization, Planet)")] string entityType,
        [Description("Related entity type being counted (e.g. Battle, Character, Starship)")] string relatedType,
        [Description("KG edge label connecting them (e.g. battle_in, member_of, fought_on)")] string label,
        [Description("'source' when entityType is the fromType in edges, 'target' when it's the toType.")] string groupBy = "source",
        [Description("Sort order: 'desc' for highest first, 'asc' for lowest first")] string sort = "desc",
        [Description("Max results (default 20, max 50)")] int limit = 20,
        [Description(ContinuityParamDescription)] string? continuity = null
    )
    {
        var results = await _kg.CountRelatedEntitiesAsync(entityType, relatedType, label, groupBySource: groupBy != "target", continuity, limit);

        if (sort == "asc")
            results.Reverse();

        return results.Select(r => new NamedCountDto(r.name, r.id, r.count)).ToList();
    }

    [Description(
        """
            Count knowledge graph nodes grouped by a property value.
            Example: 'Characters by species' → entityType='Character', property='Species'.
            Property names are infobox field names (case-sensitive, typically PascalCase).
            Call get_entity_properties on a sample entity first to discover available property names.
            Best for: Pie, Donut, Rose, Bar charts.
            """
    )]
    public async Task<List<ValueCountDto>> CountNodesByProperty(
        [Description("Entity type to aggregate (e.g. Character, Starship, CelestialBody)")] string entityType,
        [Description("Property name to group by (e.g. Species, Homeworld, Manufacturer). Case-sensitive.")] string property,
        [Description("Max results (default 20, max 50)")] int limit = 20,
        [Description(ContinuityParamDescription)] string? continuity = null
    )
    {
        var results = await _kg.CountNodesByPropertyAsync(entityType, property, continuity, limit);
        return results.Select(r => new ValueCountDto(r.value, r.count)).ToList();
    }

    [Description(
        """
            Count nodes or events bucketed by year ranges for temporal charts.
            Without 'semantic': counts by the entity's startYear (when it came into existence).
            With 'semantic': counts by a specific temporal facet for precise lifecycle queries.

            Semantic dimensions:
              lifespan.start (born), lifespan.end (died)
              conflict.start (war/battle began), conflict.end (war/battle ended)
              institutional.start (founded), institutional.end (dissolved), institutional.reorganized
              construction.start (built), construction.end (destroyed)
              creation.start (created), publication.start (released)

            Examples:
              'Characters who died per year in Clone Wars' → entityType='Character', startYear=-22, endYear=-19, semantic='lifespan.end'
              'Battles started per year' → entityType='Battle', semantic='conflict.start'
              'Ships built per decade' → entityType='Starship', bucket=10, semantic='construction.start'

            Best for: TimeSeries, Line charts.
            """
    )]
    public async Task<List<YearCountDto>> CountByYearRange(
        [Description("Entity type to count (e.g. Battle, Character, Starship)")] string entityType,
        [Description("Start year in sort-key format (-22 = 22 BBY, 4 = 4 ABY)")] int startYear,
        [Description("End year in sort-key format")] int endYear,
        [Description("Bucket size in years (1 = per year, 10 = per decade, 100 = per century)")] int bucket = 1,
        [Description(ContinuityParamDescription)] string? continuity = null,
        [Description("Optional temporal facet semantic filter (prefix-matches). Omit to use the flat startYear envelope.")] string? semantic = null
    )
    {
        var results = await _kg.CountByYearRangeAsync(entityType, startYear, endYear, bucket, continuity, semantic);
        return results.Select(r => new YearCountDto(r.year, YearDisplay(r.year), r.count)).ToList();
    }

    [Description(
        """
            Count edges between two entity types, grouped by relationship label.
            Use to discover HOW two types are connected before drilling deeper with count_related_entities.
            Example: 'How are Wars and Characters connected?' → fromType='War', toType='Character'.
            Best for: Bar, StackedBar charts.
            """
    )]
    public async Task<List<LabelCountDto>> CountEdgesBetweenTypes(
        [Description("Source entity type (e.g. War, Organization)")] string fromType,
        [Description("Target entity type (e.g. Character, Battle, Planet)")] string toType,
        [Description("Max results (default 20, max 50)")] int limit = 20,
        [Description(ContinuityParamDescription)] string? continuity = null
    )
    {
        var results = await _kg.CountEdgesBetweenTypesAsync(fromType, toType, continuity, limit);
        return results.Select(r => new LabelCountDto(r.label, r.count)).ToList();
    }

    [Description(
        """
            Rank entities by total number of relationships (edge degree).
            Example: 'Most connected characters' → entityType='Character'.
            'Most referenced planets in battles' → entityType='Planet', label='fought_on'.
            Best for: Bar, Donut charts.
            """
    )]
    public async Task<List<NamedDegreeDto>> TopConnectedEntities(
        [Description("Optional entity type filter. Omit for all types.")] string? entityType = null,
        [Description("Optional edge label filter. Omit for all labels.")] string? label = null,
        [Description("Max results (default 20, max 50)")] int limit = 20,
        [Description(ContinuityParamDescription)] string? continuity = null
    )
    {
        var results = await _kg.TopConnectedEntitiesAsync(entityType, label, continuity, limit);
        return results.Select(r => new NamedDegreeDto(r.name, r.id, r.degree)).ToList();
    }

    [Description(
        """
            Get a multi-dimensional profile of a single entity by counting edges across multiple labels.
            Returns one count per label — perfect for Radar charts comparing entities.
            Example: Clone Wars profile → entityId=<clone wars pageId>, labels=['battle_in','combatant_in','fought_on','fought_by'].
            Best for: Radar charts; also useful for StackedBar when comparing multiple entities.
            """
    )]
    public async Task<List<DimensionCountDto>> EntityProfile(
        [Description("The PageId of the entity (from search_entities)")] int entityId,
        [Description("List of edge labels to count (each becomes a Radar axis or StackedBar dimension)")] List<string> labels,
        [Description(ContinuityParamDescription)] string? continuity = null
    )
    {
        var results = await _kg.EntityProfileAsync(entityId, labels, continuity);
        return results.Select(r => new DimensionCountDto(r.dimension, r.count)).ToList();
    }

    [Description(
        """
            Count knowledge graph nodes grouped by entity type.
            Returns the distribution of entity types in the KG.
            Best for: Pie, Donut, Bar charts showing overall KG composition.
            """
    )]
    public async Task<List<TypeCountDto>> CountNodesByType([Description(ContinuityParamDescription)] string? continuity = null, [Description("Max results (default 30, max 50)")] int limit = 30)
    {
        var results = await _kg.CountNodesByTypeAsync(continuity, limit);
        return results.Select(r => new TypeCountDto(r.type, r.count)).ToList();
    }

    [Description(
        """
            For a specific named entity, get the property distribution of its connected entities.
            Example: 'Species breakdown of Jedi Order members' → rootEntityId=<Jedi Order pageId>,
              label='member_of', property='Species', rootIsTarget=true (members point TO the Jedi Order).
            'Homeworlds of Imperial officers' → rootEntityId=<Galactic Empire pageId>, label='member_of', property='Homeworld'.
            Best for: Pie, Donut, Rose, Bar charts with named-entity context.
            """
    )]
    public async Task<List<ValueCountDto>> CountPropertyForRelatedEntities(
        [Description("PageId of the root entity (e.g. the Jedi Order, Galactic Empire). Use search_entities to find it.")] int rootEntityId,
        [Description("Edge label connecting entities to the root (e.g. 'member_of', 'trained_by', 'fought_in')")] string label,
        [Description("Property to group the connected entities by (e.g. 'Species', 'Homeworld', 'Gender'). Case-sensitive.")] string property,
        [Description(
            """
                Whether the root entity is the TARGET of the edges (true) or the SOURCE (false).
                For 'member_of' edges, members are FROM and the org is TO — so rootIsTarget=true.
                For 'trained' edges from a master, the master is FROM — so rootIsTarget=false.
                """
        )]
            bool rootIsTarget = true,
        [Description("Max results (default 20, max 50)")] int limit = 20,
        [Description(ContinuityParamDescription)] string? continuity = null
    )
    {
        var results = await _kg.CountPropertyForRelatedEntitiesAsync(rootEntityId, label, property, rootIsTarget, continuity, limit);
        return results.Select(r => new ValueCountDto(r.value, r.count)).ToList();
    }

    [Description(
        """
            Group entities by what they connect to via a relationship label — returns named target entities as chart categories.
            Examples:
              'Characters grouped by faction'          → sourceType='Character', label='member_of'
              'Battles grouped by war'                 → sourceType='Battle', label='battle_in'
              'Planets grouped by governing faction'   → sourceType='CelestialBody', label='governed_by'
            Best for: Bar, Pie, Donut charts where you want named factions/orgs/wars as labels.
            """
    )]
    public async Task<List<NamedCountDto>> GroupEntitiesByConnection(
        [Description("Type of the source entities being grouped (e.g. Character, Battle, CelestialBody)")] string sourceType,
        [Description("Edge label from source to target (e.g. 'member_of', 'battle_in', 'governed_by')")] string label,
        [Description("Optional target type filter (e.g. 'Organization', 'War'). Omit for all.")] string? targetType = null,
        [Description("Max groups (default 20, max 50)")] int limit = 20,
        [Description(ContinuityParamDescription)] string? continuity = null
    )
    {
        var results = await _kg.GroupEntitiesByConnectionAsync(sourceType, label, targetType, continuity, limit);
        return results.Select(r => new NamedCountDto(r.name, r.id, r.count)).ToList();
    }

    [Description(
        """
            Compare multiple named entities side-by-side across relationship dimensions.
            Returns one series per entity with counts for each label — perfect for Radar and StackedBar comparisons.
            Example: 'Compare Yoda vs Palpatine vs Dooku' → entityIds=[yodaId, palpatineId, dookuId],
              labels=['trained','trained_by','member_of','fought_in','allied_with'].
            Best for: Radar (overlay profiles), StackedBar (side-by-side dimensions).
            """
    )]
    public async Task<List<EntityComparisonDto>> CompareEntities(
        [Description("PageIds of the entities to compare (max 10). Use search_entities to find them.")] List<int> entityIds,
        [Description("Edge labels to measure for each entity (each becomes an axis/dimension)")] List<string> labels,
        [Description(ContinuityParamDescription)] string? continuity = null
    )
    {
        var results = await _kg.CompareEntitiesAsync(entityIds, labels, continuity);

        return results.Select(r => new EntityComparisonDto(EntityId: r.entityId, Name: r.entityName, Dimensions: r.dimensions.Select(d => new DimensionCountDto(d.label, d.count)).ToList())).ToList();
    }

    [Description(
        """
            Get detailed info about relationship labels: which entity types they connect, their
            reverse labels, descriptions, and usage counts.
            ESSENTIAL discovery tool: call this BEFORE analytics queries to understand which labels
            connect which types and in which direction.
            Without arguments, returns the top labels by usage.
            """
    )]
    public async Task<List<RelationshipLabelDto>> DescribeRelationshipLabels(
        [Description("Optional list of specific label names to describe. If omitted, returns the top labels by usage count.")] List<string>? labels = null,
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

        return results
            .Select(l => new RelationshipLabelDto(Label: l.Label, Reverse: l.Reverse, Description: l.Description, FromTypes: l.FromTypes, ToTypes: l.ToTypes, UsageCount: l.UsageCount))
            .ToList();
    }

    [Description(
        """
            Introspect the schema of an entity type — which infobox fields it has, classified as
            properties/relationships/temporal facets, plus which labels are primary for that type.
            Use this to understand what data is available for a type before writing queries.
            Example: describe_entity_schema('Battle') shows commanders1/2, side1/2, unit1/2,
            Date (temporal), Outcome (property), etc.
            """
    )]
    public EntitySchemaDto DescribeEntitySchema([Description("Entity type name, e.g. 'Battle', 'Character', 'Government'")] string type)
    {
        var def = InfoboxDefinitionRegistry.ForTemplate(type);
        var primary = DefaultLabelSelector.GetDefaults(type, def.Relationships.Values.Select(r => r.Label).Distinct()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new EntitySchemaDto(
            Type: type,
            Properties: def.Properties.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
            TemporalFields: def.TemporalFields.Select(kv => new TemporalFieldDto(kv.Key, kv.Value.Semantic, kv.Value.Calendar)).OrderBy(x => x.Field, StringComparer.OrdinalIgnoreCase).ToList(),
            Relationships: def.Relationships.Select(kv => new SchemaRelationshipDto(
                    Field: kv.Key,
                    Label: kv.Value.Label,
                    Reverse: kv.Value.Reverse,
                    Targets: kv.Value.ExpectedTargetTypes?.ToList(),
                    Category: kv.Value.Category,
                    Description: kv.Value.Description,
                    Primary: primary.Contains(kv.Value.Label)
                ))
                .OrderBy(x => x.Field, StringComparer.OrdinalIgnoreCase)
                .ToList()
        );
    }

    [Description(
        """
            Find entities that experienced a specific lifecycle transition within a year range,
            using temporal facet semantics.
            Example: 'Governments reorganized between 20 BBY and 4 BBY' →
              semantic='institutional.reorganized', startYear=-20, endYear=-4.

            Semantic dimensions and roles:
              lifespan.start/end                   (born/died)
              conflict.start/end/point             (war began/ended/happened)
              institutional.start/end/reorganized/restored/fragmented/suspended
              construction.start/end/rebuilt       (built/destroyed)
              creation.start/end/discovered        (device created/retired/discovered)
              publication.release/start/end        (media released/first-issue/last-issue)
              usage.start/end                      (first/last employed)

            Returns entities with the facet text so you can see the transition reason.
            """
    )]
    public async Task<List<LifecycleTransitionDto>> FindByLifecycleTransition(
        [Description("Exact semantic dimension.role (e.g. 'institutional.reorganized', 'lifespan.end', 'conflict.start')")] string semantic,
        [Description("Start year in sort-key format (-19 = 19 BBY, 4 = 4 ABY, or CE year for publication)")] int startYear,
        [Description("End year in sort-key format")] int endYear,
        [Description("Optional entity type filter (e.g. 'Government', 'Character'). Omit for all.")] string? entityType = null,
        [Description("Max results (default 30, max 100)")] int limit = 30,
        [Description(ContinuityParamDescription)] string? continuity = null
    )
    {
        var results = await _kg.FindByLifecycleTransitionAsync(semantic, startYear, endYear, entityType, continuity, Math.Min(limit, 100));

        return results
            .Select(r => new LifecycleTransitionDto(PageId: r.pageId, Name: r.name, Type: r.type, Transition: new LifecycleTransitionDetailDto(r.semantic, r.year, r.text, r.calendar)))
            .ToList();
    }

    [Description(
        """
            Count lifecycle transitions per year bucket for time-series charts.
            Example: 'Governments reorganized per decade' → semantic='institutional.reorganized', bucket=10.
            'Characters who died per year during the Clone Wars' → semantic='lifespan.end', startYear=-22, endYear=-19, bucket=1.

            More precise than count_by_year_range for lifecycle transitions because it directly
            counts the semantic facets rather than relying on the node's startYear envelope.
            Best for: TimeSeries, Line charts.
            """
    )]
    public async Task<List<YearCountDto>> CountLifecycleTransitions(
        [Description("Semantic dimension.role, e.g. 'institutional.reorganized', 'lifespan.end', 'conflict.start'")] string semantic,
        [Description("Start year in sort-key format")] int startYear,
        [Description("End year in sort-key format")] int endYear,
        [Description("Bucket size in years (1 = per year, 10 = per decade, 100 = per century)")] int bucket = 1,
        [Description("Optional entity type filter")] string? entityType = null,
        [Description(ContinuityParamDescription)] string? continuity = null
    )
    {
        var results = await _kg.CountLifecycleTransitionsAsync(semantic, startYear, endYear, bucket, entityType, continuity);
        return results.Select(r => new YearCountDto(r.year, YearDisplay(r.year), r.count)).ToList();
    }

    [Description(
        """
            List all edge labels that belong to a specific semantic category.
            Categories declared in FieldSemantics: family, mentorship, military, political, location,
            astronomy, biological, cultural, religion, economic, publication, creator, possession,
            usage, sports, music, food, temporal, sequence, composition, medical, honors.
            Example: list_labels_by_category('family') → child_of, parent_of, sibling_of, partner_of, has_relative, family.
            """
    )]
    public LabelsByCategoryDto ListLabelsByCategory([Description("Category name (e.g. 'family', 'military', 'publication')")] string category)
    {
        var labels = FieldSemantics
            .Relationships.Values.Where(d => string.Equals(d.Category, category, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(d => d.Label)
            .Select(d => new CategoryLabelDto(Label: d.Label, Reverse: d.Reverse, Description: d.Description, Targets: d.ExpectedTargetTypes?.ToList()))
            .OrderBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new LabelsByCategoryDto(category, labels.Count, labels);
    }

    [Description(
        """
            Get an entity's relationships narrowed to a single semantic category.
            Returns connected entities grouped by label, scoped to one topical lens.
            Example: Anakin's military relationships → category='military'.
            Use instead of get_entity_relationships when you only want one topic —
            avoids noise from unrelated relationship categories.
            """
    )]
    public async Task<RelationshipsByCategoryDto> GetRelationshipsByCategory(
        [Description("The PageId of the entity (from search_entities)")] int entityId,
        [Description("Semantic category: family, mentorship, military, political, location, publication, etc.")] string category,
        [Description(ContinuityParamDescription)] string? continuity = null,
        [Description("Max edges (default 50, max 200)")] int limit = 50
    )
    {
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
            return new RelationshipsByCategoryDto(
                EntityId: entityId,
                EntityName: null,
                Category: category,
                Relationships: new Dictionary<string, List<CategoryRelationshipTargetDto>>(),
                TotalEdges: 0,
                Note: $"No labels registered under category '{category}'. Try list_labels_by_category to see valid categories."
            );

        var labelFilter = string.Join(",", categoryLabels);
        var edges = await _kg.GetAllEdgesForEntityAsync(entityId, labelFilter, continuity, Math.Min(limit, 200));

        if (edges.Count == 0)
            return new RelationshipsByCategoryDto(
                EntityId: entityId,
                EntityName: null,
                Category: category,
                Relationships: new Dictionary<string, List<CategoryRelationshipTargetDto>>(),
                TotalEdges: 0
            );

        var grouped = edges.GroupBy(e => e.Label).ToDictionary(g => g.Key, g => g.Select(e => new CategoryRelationshipTargetDto(e.ToId, e.ToName, e.ToType, e.FromYear, e.ToYear)).ToList());

        return new RelationshipsByCategoryDto(EntityId: entityId, EntityName: edges[0].FromName, Category: category, Relationships: grouped, TotalEdges: edges.Count);
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
