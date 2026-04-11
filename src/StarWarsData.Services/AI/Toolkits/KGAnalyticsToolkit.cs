using System.ComponentModel;
using Microsoft.Extensions.AI;
using MongoDB.Bson;
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

    /// <summary>
    /// Properties that are genuinely categorical (few distinct values expected).
    /// The sparse-redirect should NOT fire for these — few values is correct, not a sign
    /// that the data is modeled as edges.
    /// </summary>
    static bool IsCategoricalProperty(string property) =>
        property.Equals("Era", StringComparison.OrdinalIgnoreCase)
        || property.Equals("Period", StringComparison.OrdinalIgnoreCase)
        || property.Equals("Epoch", StringComparison.OrdinalIgnoreCase)
        || property.Equals("Phase", StringComparison.OrdinalIgnoreCase)
        || property.Equals("Alignment", StringComparison.OrdinalIgnoreCase)
        || property.Equals("Gender", StringComparison.OrdinalIgnoreCase)
        || property.Equals("Outcome", StringComparison.OrdinalIgnoreCase);

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
            Count nodes grouped by MULTIPLE properties simultaneously, with the contributing
            source nodes attached to every row so the agent can cite them in `references`.
            Each row contains the property combination, a count, and a `sources` array of
            up to `maxSources` contributing nodes (pageId + title + wikiUrl). Use these wikiUrls
            to populate `references` on render_chart / render_data_table — never invent citations.

            Example: 'Starship classes with manufacturer, count, and example ships' →
                entityType='Starship', properties=['Class', 'Manufacturer']

            ONE call returns all combinations — never loop through entities individually.
            Best for: render_data_table with multi-column grouping.
            """
    )]
    public async Task<List<Dictionary<string, object>>> CountNodesByProperties(
        [Description("Entity type to aggregate (e.g. Starship, Character, CelestialBody)")] string entityType,
        [Description("Property names to group by (e.g. ['Class', 'Manufacturer']). Case-sensitive.")] List<string> properties,
        [Description("Max contributing source nodes attached per row (default 5, max 25). Use 1 if you only need a single citation per row.")] int maxSources = 5,
        [Description("Max results (default 30, max 50)")] int limit = 30,
        [Description(ContinuityParamDescription)] string? continuity = null
    )
    {
        var results = await _kg.CountNodesByPropertiesAsync(entityType, properties, continuity, maxSources, limit);

        return results
            .Select(doc =>
            {
                var row = new Dictionary<string, object>();
                var id = doc["_id"].AsBsonDocument;
                foreach (var prop in properties)
                {
                    var val = id.GetValue(prop, BsonNull.Value);
                    row[prop] =
                        val.IsBsonNull ? "Unknown"
                        : val.IsBsonArray ? string.Join(", ", val.AsBsonArray.Select(v => v.AsString))
                        : val.AsString;
                }
                row["count"] = doc["count"].AsInt32;
                row["sources"] = ReadSourcesFromDoc(doc);
                return row;
            })
            .ToList();
    }

    static List<NodeRefDto> ReadSourcesFromDoc(BsonDocument doc)
    {
        if (!doc.Contains("sources") || doc["sources"].IsBsonNull)
            return [];
        return doc["sources"]
            .AsBsonArray.Select(s =>
            {
                var sd = s.AsBsonDocument;
                return new NodeRefDto(
                    PageId: sd.GetValue("pageId", 0).ToInt32(),
                    Title: sd.GetValue("title", "").IsBsonNull ? "" : sd["title"].AsString,
                    WikiUrl: sd.GetValue("wikiUrl", BsonNull.Value).IsBsonNull ? null : sd["wikiUrl"].AsString
                );
            })
            .ToList();
    }

    [Description(
        """
            PRIMARY TOOL for property aggregation on the KG. Call this for ANY property you want
            to group entities by — it's safe to call even if you're not sure whether the field
            is a true scalar or a link-bearing field that's been promoted to an edge. The
            response is SELF-CORRECTING: if the property aggregation comes back sparse and a
            stronger edge label exists for that source type, the response carries a Note plus
            RecommendedEdgeLabels pointing you to group_entities_by_connection.

            Read the response in this order:
              1. If `note` is populated → the property data is sparse. Switch to
                 group_entities_by_connection(sourceType=<entityType>, label=<top recommended
                 edge label>) and discard `results`. Do NOT chart the sparse residual.
              2. If `note` is null → `results` is the canonical answer. Each row has
                 up to `maxSources` contributing nodes (pageId + title + wikiUrl) for citations.

            Examples that work directly (no redirect):
              'ForcePower by Alignment' → entityType='ForcePower', property='Alignment' (170 rows)
              'Character by Gender'     → entityType='Character', property='Gender' (33k+ rows)
              'Battle by Outcome'       → entityType='Battle', property='Outcome' (3500+ rows)

            Examples that will trigger a redirect (sparse property → edge label hint):
              'Character by Species'    → property aggregation has 48 residuals; note will say
                                          "use group_entities_by_connection with label=species"
              'Food by Place of origin' → property aggregation is empty; note will redirect
                                          to label=originates_from / found_at

            AFTER THIS TOOL RETURNS → call render_chart (when a chart was requested) or
            render_data_table (for tabular output). Do NOT write the counts as plain text.

            EXCEPTION — categorical properties (Era, Period, Epoch, Phase, Alignment, Gender):
            These naturally have few values (< 20). If the sparse-redirect note fires for one
            of these, IGNORE IT — chart the results directly. The redirect is wrong here because
            the property is genuinely categorical, not a mis-modeled edge.

            Best for: Pie, Donut, Rose, Bar charts. Trust the note for non-categorical fields.
            """
    )]
    public async Task<CountNodesByPropertyResult> CountNodesByProperty(
        [Description("Entity type to aggregate (e.g. Character, Starship, CelestialBody, ForcePower)")] string entityType,
        [Description("Property name to group by (e.g. Alignment, Gender, Homeworld, Manufacturer). Case-sensitive.")] string property,
        [Description("Max results (default 20, max 50)")] int limit = 20,
        [Description("Max contributing source nodes attached per row (default 5, max 25). Use 1 if you only need a single citation per row.")] int maxSources = 5,
        [Description(ContinuityParamDescription)] string? continuity = null
    )
    {
        var raw = await _kg.CountNodesByPropertyAsync(entityType, property, continuity, limit, maxSources);
        var results = raw.Select(r => new ValueCountDto(r.value, r.count, r.sources)).ToList();
        var totalMatched = results.Sum(r => r.Count);

        // Self-correcting hint: when the property aggregation came back sparse, check whether
        // there's a much stronger edge label on this source type. If so, the agent should be
        // redirected to group_entities_by_connection instead of charting the residual.
        // "Sparse" = total matched count is small in absolute terms (< 20). For genuinely
        // populated scalar fields like Alignment (170 rows on ForcePower), no hint fires.
        //
        // EXCEPTION: categorical properties (Era, Period, Epoch, Alignment, Gender, Outcome)
        // naturally have few distinct values — the sparse redirect is a false positive for these.
        if (totalMatched < 20 && !IsCategoricalProperty(property))
        {
            var edgeLabels = await _kg.GetEdgeLabelsForSourceTypeAsync(entityType, continuity);
            // Find labels that have substantially more data than the property — these are the
            // ones the agent should switch to. Cap at top 3 to keep the hint concise.
            var stronger = edgeLabels.Where(e => e.count >= Math.Max(totalMatched * 5, 10)).Take(3).Select(e => new LabelCountDto(e.label, e.count)).ToList();

            if (stronger.Count > 0)
            {
                var topAlt = stronger[0];
                var note =
                    $"Property '{property}' on type '{entityType}' aggregated to only {totalMatched} entries — "
                    + $"this field appears to be stored as an edge in kg.edges, not as a node property. "
                    + $"Switch to: group_entities_by_connection(sourceType='{entityType}', label='{topAlt.Label}') "
                    + $"which has {topAlt.Count} edges. Other candidate labels: "
                    + string.Join(", ", stronger.Skip(1).Select(s => $"{s.Label} ({s.Count})"))
                    + ". DO NOT chart the sparse `results` — they are misleading.";
                return new CountNodesByPropertyResult(results, totalMatched, stronger, note);
            }
        }

        // For categorical properties with zero results, suggest a year-range fallback
        if (totalMatched == 0 && IsCategoricalProperty(property))
        {
            var note =
                $"Property '{property}' on type '{entityType}' returned 0 results — this property "
                + $"may not be populated for this entity type. Try count_by_year_range to partition "
                + $"'{entityType}' entities by time period instead, then render_chart with those results.";
            return new CountNodesByPropertyResult(results, totalMatched, Note: note);
        }

        return new CountNodesByPropertyResult(results, totalMatched);
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

            Best for: TimeSeries, Line, Pie, Bar charts. When the user asked for a Pie/Bar chart and this returns data, ALWAYS follow with render_chart — year-range buckets are valid chart categories.
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
            Each result row carries up to `maxSources` contributing nodes (pageId + title + wikiUrl)
            so the agent can cite them in `references` on the render tool. When `count: 1`, that
            single source IS the answer for that bucket — always cite it. Use these wikiUrls —
            never invent citations.

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
        [Description("Max contributing source nodes attached per row (default 5, max 25). Use 1 if you only need a single citation per row.")] int maxSources = 5,
        [Description(ContinuityParamDescription)] string? continuity = null
    )
    {
        var results = await _kg.CountPropertyForRelatedEntitiesAsync(rootEntityId, label, property, rootIsTarget, continuity, limit, maxSources);
        return results.Select(r => new ValueCountDto(r.value, r.count, r.sources)).ToList();
    }

    [Description(
        """
            PRIMARY TOOL for aggregation grouped by a LINKED entity. This is the right tool
            whenever the grouping dimension is a field that points to another entity — Species,
            Homeworld, Affiliation, Place, Origin, Faction, War, Manufacturer, etc. If you were
            about to call count_nodes_by_property for a field that references another entity,
            call THIS instead. Queries kg.edges directly and returns real, canonical counts
            grouped by the target entity's name.

            Examples:
              'Characters by Species'                 → sourceType='Character', label='species'
              'Characters by Homeworld'               → sourceType='Character', label='homeworld'
              'Characters grouped by faction'         → sourceType='Character', label='member_of'
              'Battles grouped by war'                → sourceType='Battle',   label='battle_in'
              'Food by Place of origin'               → sourceType='Food',     label='originates_from'
              'Planets grouped by governing faction'  → sourceType='CelestialBody', label='governed_by'

            Best for: Bar, Pie, Donut charts where the X-axis is a named entity (faction, war,
            planet, species, etc). Call describe_relationship_labels first if you're unsure
            which edge label a field has been normalized to.
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

        // Batch-enrich target nodes with wikiUrl so each relationship target is directly citable
        // from this single tool call — no follow-up search_entities needed to build references.
        var targetIds = edges.Select(e => e.ToId).Distinct().ToList();
        var enrichment = await _kg.GetNodePropertiesBatchAsync(targetIds);

        var grouped = edges
            .GroupBy(e => e.Label)
            .ToDictionary(
                g => g.Key,
                g =>
                    g.Select(e =>
                        {
                            enrichment.TryGetValue(e.ToId, out var info);
                            return new CategoryRelationshipTargetDto(e.ToId, e.ToName, e.ToType, e.FromYear, e.ToYear, WikiUrl: info?.WikiUrl);
                        })
                        .ToList()
            );

        return new RelationshipsByCategoryDto(EntityId: entityId, EntityName: edges[0].FromName, Category: category, Relationships: grouped, TotalEdges: edges.Count);
    }

    public IReadOnlyList<AITool> AsAIFunctions() =>
        [
            AIFunctionFactory.Create(DescribeRelationshipLabels, ToolNames.KGAnalytics.DescribeRelationshipLabels),
            AIFunctionFactory.Create(CountRelatedEntities, ToolNames.KGAnalytics.CountRelatedEntities),
            AIFunctionFactory.Create(CountNodesByProperty, ToolNames.KGAnalytics.CountNodesByProperty),
            AIFunctionFactory.Create(CountByYearRange, ToolNames.KGAnalytics.CountByYearRange),
            AIFunctionFactory.Create(CountEdgesBetweenTypes, ToolNames.KGAnalytics.CountEdgesBetweenTypes),
            AIFunctionFactory.Create(TopConnectedEntities, ToolNames.KGAnalytics.TopConnectedEntities),
            AIFunctionFactory.Create(EntityProfile, ToolNames.KGAnalytics.EntityProfile),
            AIFunctionFactory.Create(CountNodesByType, ToolNames.KGAnalytics.CountNodesByType),
            AIFunctionFactory.Create(CountPropertyForRelatedEntities, ToolNames.KGAnalytics.CountPropertyForRelatedEntities),
            AIFunctionFactory.Create(GroupEntitiesByConnection, ToolNames.KGAnalytics.GroupEntitiesByConnection),
            AIFunctionFactory.Create(CompareEntities, ToolNames.KGAnalytics.CompareEntities),
            AIFunctionFactory.Create(DescribeEntitySchema, ToolNames.KGAnalytics.DescribeEntitySchema),
            AIFunctionFactory.Create(FindByLifecycleTransition, ToolNames.KGAnalytics.FindByLifecycleTransition),
            AIFunctionFactory.Create(CountLifecycleTransitions, ToolNames.KGAnalytics.CountLifecycleTransitions),
            AIFunctionFactory.Create(ListLabelsByCategory, ToolNames.KGAnalytics.ListLabelsByCategory),
            AIFunctionFactory.Create(GetRelationshipsByCategory, ToolNames.KGAnalytics.GetRelationshipsByCategory),
        ];
}
