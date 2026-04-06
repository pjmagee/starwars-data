using System.ComponentModel;
using Microsoft.Extensions.AI;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services;

/// <summary>
/// AI tools for knowledge-graph-backed retrieval and semantic search.
///
/// This is the primary lookup toolkit for the Ask AI agent. Prefer these tools
/// over raw <c>DataExplorer</c> queries because the KG nodes carry temporal facets,
/// normalized entity types, and bidirectional edges.
///
/// Years use a sort-key format: negative = BBY, positive = ABY, or the CE year
/// (e.g. 2015) for publication dates.
/// </summary>
public class GraphRAGToolkit
{
    readonly KnowledgeGraphQueryService _kg;
    readonly SemanticSearchService _search;
    readonly IMongoCollection<GalaxyYearDocument> _galaxyYears;
    readonly IMongoCollection<BsonDocument> _galaxyYearsRaw;

    const string ContinuityParamDescription = "Optional continuity filter: Canon, Legends, or omit for all";

    public GraphRAGToolkit(KnowledgeGraphQueryService kg, SemanticSearchService search, IMongoClient mongoClient, string databaseName)
    {
        _kg = kg;
        _search = search;
        var db = mongoClient.GetDatabase(databaseName);
        _galaxyYears = db.GetCollection<GalaxyYearDocument>(Collections.GalaxyYears);
        _galaxyYearsRaw = db.GetCollection<BsonDocument>(Collections.GalaxyYears);
    }

    static KgTemporalFacetDto ToFacetDto(Models.Entities.TemporalFacet f, bool includeOrder = false) => new(f.Field, f.Semantic, f.Calendar, f.Year, f.Text, includeOrder ? f.Order : null);

    static KgNodeDto ToNodeDto(GraphNode n) =>
        new(
            PageId: n.PageId,
            Name: n.Name,
            Type: n.Type,
            Continuity: n.Continuity.ToString(),
            ImageUrl: n.ImageUrl,
            WikiUrl: n.WikiUrl,
            StartYear: n.StartYear,
            EndYear: n.EndYear,
            TemporalFacets: n.TemporalFacets.Select(f => ToFacetDto(f)).ToList()
        );

    [Description(
        """
            Search the knowledge graph for entities by name. Returns entities with their type,
            properties, and temporal lifecycle. Use to resolve entity names to PageIds before
            calling other graph tools.
            """
    )]
    public async Task<List<KgNodeDto>> SearchEntities(
        [Description("Entity name to search for (case-insensitive partial match)")] string query,
        [Description("Optional entity type filter (e.g. Character, Organization, CelestialBody)")] string? type = null,
        [Description(ContinuityParamDescription)] string? continuity = null,
        [Description("Max results (default 10)")] int limit = 10
    )
    {
        var results = await _kg.SearchNodesAsync(query, type, continuity, Math.Min(limit, 20));
        return results.Select(ToNodeDto).ToList();
    }

    [Description(
        """
            Find entities that existed during a year or year range.
            Pass a single year OR a range (year + yearEnd) to find everything active in that window.

            The 'semantic' parameter filters by temporal dimension:
              lifespan      = who was alive
              conflict      = battles/wars/missions happening
              institutional = governments/organizations existing
              construction  = structures/ships built
              creation      = artifacts/devices existing
              publication   = media released (real-world CE years)

            Examples:
              'Characters alive in 19 BBY' → year=-19, type='Character', semantic='lifespan'
              'Wars during 4000-1000 BBY'  → year=-4000, yearEnd=-1000, type='War', semantic='conflict'
              'Books published in 2015'    → year=2015, type='Book', semantic='publication'
            """
    )]
    public async Task<List<KgNodeDto>> FindEntitiesByYear(
        [Description("Start year or single year (sort-key: -19 = 19 BBY, 4 = 4 ABY, or CE year for publications)")] int year,
        [Description("Entity type filter (e.g. Character, Government, Organization, Battle, War, Book)")] string type,
        [Description("Optional end year for range queries. Omit to query a single year.")] int? yearEnd = null,
        [Description(ContinuityParamDescription)] string? continuity = null,
        [Description("Optional temporal dimension: lifespan, conflict, institutional, construction, creation, publication. Omit to use the flat startYear/endYear envelope.")] string? semantic = null,
        [Description("Max results (default 20)")] int limit = 20
    )
    {
        var results = await _kg.FindNodesByYearAsync(year, type, yearEnd, continuity, semantic, limit);
        return results.Select(ToNodeDto).ToList();
    }

    [Description(
        """
            Get properties (attributes) of a knowledge graph entity — height, eye color, classification, etc.
            Use for factual questions about an entity's characteristics. Call search_entities first to get the PageId.
            """
    )]
    public async Task<KgNodeDetailDto> GetEntityProperties([Description("The PageId of the entity (from search_entities)")] int entityId)
    {
        var node = await _kg.GetNodeByIdAsync(entityId);
        if (node is null)
            return new KgNodeDetailDto(null, null, null, null, null, null, null, null, null, null, Error: "Entity not found in knowledge graph.");

        return new KgNodeDetailDto(
            PageId: node.PageId,
            Name: node.Name,
            Type: node.Type,
            Continuity: node.Continuity.ToString(),
            Properties: node.Properties,
            StartYear: node.StartYear,
            EndYear: node.EndYear,
            TemporalFacets: node.TemporalFacets.Select(f => ToFacetDto(f, includeOrder: true)).ToList(),
            ImageUrl: node.ImageUrl,
            WikiUrl: node.WikiUrl
        );
    }

    [Description(
        """
            Get all direct relationships for an entity, grouped by relationship label.
            BIDIRECTIONAL: returns both outgoing edges AND inbound edges rewritten with their
            reverse label so they read from the entity's perspective. For example, battles
            commanded by Anakin (stored as Battle → commanded_by → Anakin) appear on Anakin as
            'commanded → Battle'.
            Label filter accepts either the forward (commanded_by) or reverse (commanded) form.
            Use for 'Who trained X?', 'What battles did X command?', 'What ships were built by X?'.
            """
    )]
    public async Task<EntityRelationshipsDto> GetEntityRelationships(
        [Description("The PageId of the entity (from search_entities)")] int entityId,
        [Description("Optional comma-separated labels to filter (e.g. 'parent_of,commanded,trained_by'). Reverse forms are resolved automatically.")] string? labelFilter = null,
        [Description(ContinuityParamDescription)] string? continuity = null,
        [Description("Max edges to return (default 40, max 100)")] int limit = 40
    )
    {
        var edges = await _kg.GetAllEdgesForEntityAsync(entityId, labelFilter, continuity, limit);

        if (edges.Count == 0)
            return new EntityRelationshipsDto(
                EntityId: entityId,
                EntityName: null,
                Relationships: new Dictionary<string, List<RelationshipTargetDto>>(),
                TotalEdges: 0,
                Note: "No relationships found (outgoing or inbound). The entity may not have been processed, or the label filter excluded everything."
            );

        var grouped = edges
            .GroupBy(e => e.Label)
            .ToDictionary(g => g.Key, g => g.Select(e => new RelationshipTargetDto(e.ToId, e.ToName, e.ToType, Math.Round(e.Weight, 2), Truncate(e.Evidence, 200))).ToList());

        return new EntityRelationshipsDto(EntityId: entityId, EntityName: edges[0].FromName, Relationships: grouped, TotalEdges: edges.Count);
    }

    [Description(
        """
            List what types of relationships an entity has in the knowledge graph.
            Returns each relationship label with its count and description.
            Use to discover available relationship types before filtering with get_entity_relationships.
            """
    )]
    public async Task<List<RelationshipTypeDto>> GetRelationshipTypes(
        [Description("The PageId of the entity (from search_entities)")] int entityId,
        [Description(ContinuityParamDescription)] string? continuity = null
    )
    {
        var results = await _kg.GetRelationshipTypesAsync(entityId, continuity);
        return results.Select(r => new RelationshipTypeDto(r.label, r.count, Math.Round(r.avgWeight, 2))).ToList();
    }

    [Description(
        """
            Traverse the relationship graph from an entity up to N hops deep.
            Returns all connected entities and their relationships as context for answering questions.
            Use for broad exploration ('Show me everyone connected to Yoda', 'What is Palpatine's network?').
            For simple direct relationships, prefer get_entity_relationships.

            Optionally restrict to a temporal window via yearFrom/yearTo (sort-key years, negative = BBY).
            Edges without temporal bounds are still included — the filter only prunes edges whose
            known [fromYear, toYear] interval falls entirely outside the window.
            """
    )]
    public async Task<GraphTraversalDto> TraverseGraph(
        [Description("The PageId of the starting entity (from search_entities)")] int entityId,
        [Description("Optional comma-separated labels to follow (e.g. 'trained_by,trained'). Omit for all.")] string? labels = null,
        [Description("Max traversal depth, 1-3 (default 2). Higher values return more data.")] int maxDepth = 2,
        [Description(ContinuityParamDescription)] string? continuity = null,
        [Description("Optional start of temporal window (sort-key year, e.g. -22 for 22 BBY). Edges whose interval ends before this are pruned. Null bounds on edges always pass.")]
            int? yearFrom = null,
        [Description("Optional end of temporal window (sort-key year). Edges whose interval starts after this are pruned.")] int? yearTo = null
    )
    {
        var result = await _kg.QueryGraphAsync(entityId, labels, maxDepth, continuity, yearFrom: yearFrom, yearTo: yearTo, ct: default);

        return new GraphTraversalDto(
            Root: new GraphTraversalRootDto(entityId),
            Nodes: result.Nodes.Select(n => new GraphNodeDto(n.Id, n.Name, n.Type)).ToList(),
            Edges: result.Edges.Select(e => new GraphEdgeDto(e.FromId, e.ToId, e.Label, Math.Round(e.Weight, 2))).ToList(),
            Summary: $"Found {result.Nodes.Count} connected entities and {result.Edges.Count} relationships across {maxDepth} hops"
        );
    }

    [Description(
        """
            Walk a hierarchy-shaped relationship (e.g. apprentice_of, parent_of, successor_of,
            predecessor_of, member_of) from an entity and return the full chain up to N hops
            ordered by depth. Use for lineage questions like 'Yoda's master chain',
            'successors of the Galactic Republic', 'Palpatine's apprentices down the line'.

            Direction is mechanical (not semantic), because different labels have different
            semantic directions:
              - 'forward'  walks along the stored edge direction (root is the fromId of the
                            first hop; next seed is each edge's toId).
              - 'reverse'  walks against the stored edge direction (root is the toId of the
                            first hop; next seed is each edge's fromId).

            Concrete examples:
              apprentice_of  forward → yields Yoda's masters (apprentice → master → grandmaster).
              apprentice_of  reverse → yields Yoda's apprentices (master → apprentice → ...).
              parent_of      forward → yields descendants (parent → child → grandchild).
              parent_of      reverse → yields ancestors (child → parent → grandparent).
              successor_of   forward → yields successor chain (older → newer).
              successor_of   reverse → yields predecessor chain.

            This is the right tool for any single-label lineage question. For broader
            neighborhood exploration across many labels, use traverse_graph.
            """
    )]
    public async Task<LineageDto> GetLineage(
        [Description("The PageId of the starting entity (from search_entities)")] int entityId,
        [Description(
            "Relationship label to follow (e.g. 'apprentice_of', 'parent_of', 'successor_of'). Must be a forward label stored in kg.edges — use list_relationship_labels to discover valid values."
        )]
            string label,
        [Description("'forward' walks along the stored edge direction; 'reverse' walks against it. See tool description for per-label examples.")] string direction = "forward",
        [Description("Max hops from root, 1-10 (default 5).")] int maxDepth = 5,
        [Description(ContinuityParamDescription)] string? continuity = null
    )
    {
        if (!string.Equals(direction, "forward", StringComparison.OrdinalIgnoreCase) && !string.Equals(direction, "reverse", StringComparison.OrdinalIgnoreCase))
            return new LineageDto(entityId, string.Empty, label, direction, 0, [], Note: "direction must be 'forward' or 'reverse'.");

        var result = await _kg.GetLineageAsync(entityId, label, direction, maxDepth, continuity);

        if (result.Chain.Count == 0)
            return new LineageDto(
                result.RootId,
                result.RootName,
                result.Label,
                result.Direction,
                0,
                [],
                Note: $"No '{label}' chain found {result.Direction} from '{result.RootName}'. The label may not apply to this entity or the chain may be empty."
            );

        var steps = result
            .Chain.Select(s => new LineageStepDto(Hop: s.Hop, FromId: s.FromId, FromName: s.FromName, FromType: s.FromType, ToId: s.ToId, ToName: s.ToName, ToType: s.ToType, Label: s.Label))
            .ToList();

        return new LineageDto(RootId: result.RootId, RootName: result.RootName, Label: result.Label, Direction: result.Direction, Depth: steps.Max(s => s.Hop), Chain: steps);
    }

    [Description(
        """
            Find how two entities are connected in the knowledge graph.
            Returns the shortest path between them with relationship labels and evidence.
            Use for 'How is Palpatine connected to Luke Skywalker?' or
            'What is the relationship between the Jedi Order and the Republic?'.

            Optionally restrict the path to edges active within a temporal window via yearFrom/yearTo
            (sort-key years, negative = BBY). Useful for 'How was Anakin connected to Padmé between
            22 BBY and 19 BBY?'. Edges without temporal bounds are still considered.
            """
    )]
    public async Task<ConnectionsDto> FindConnections(
        [Description("PageId of the first entity")] int entityId1,
        [Description("PageId of the second entity")] int entityId2,
        [Description("Maximum path length to search (default 3, max 4)")] int maxHops = 3,
        [Description(ContinuityParamDescription)] string? continuity = null,
        [Description("Optional start of temporal window (sort-key year).")] int? yearFrom = null,
        [Description("Optional end of temporal window (sort-key year).")] int? yearTo = null
    )
    {
        if (entityId1 == entityId2)
            return new ConnectionsDto(Connected: true, PathLength: 0, Path: [], Note: "Same entity");

        var (connected, path) = await _kg.FindConnectionsAsync(entityId1, entityId2, maxHops, continuity, yearFrom, yearTo);

        if (!connected)
            return new ConnectionsDto(Connected: false, SearchedHops: maxHops, Note: $"No connection found within {maxHops} hops.");

        return new ConnectionsDto(Connected: true, PathLength: path.Count, Path: path.Select(s => new ConnectionStepDto(s.fromName, s.toName, s.label, Truncate(s.evidence, 200))).ToList());
    }

    [Description(
        """
            Semantic search over 800K+ wiki article passages using AI embeddings.
            Finds content by MEANING, not keywords — understands concepts, paraphrases, and indirect references.
            Returns the most relevant passages with title, wikiUrl, sectionUrl (deep link), and text.
            PREFER THIS over keyword_search for why/how questions, lore, philosophy, motivations, and narrative context.
            Combine with KG tools for comprehensive answers: KG for structured facts + semantic_search for narrative depth.
            """
    )]
    public async Task<SemanticSearchResultDto> SemanticSearch(
        [Description("Natural language search query, e.g. 'Battle of Endor aftermath', 'Darth Vader's redemption'")] string query,
        [Description("Optional entity type filter (e.g. Character, Planet, Battle). Omit for all types.")] string? type = null,
        [Description(ContinuityParamDescription)] string? continuity = null,
        [Description("Max results (default 5, max 10)")] int limit = 5
    )
    {
        limit = Math.Clamp(limit, 1, 10);

        var types = !string.IsNullOrWhiteSpace(type) ? new[] { type } : null;
        Continuity? cont = continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var c) ? c : null;

        var results = await _search.SearchAsync(query, types, cont, limit: limit);

        if (results.Count == 0)
            return new SemanticSearchResultDto(Query: query, Results: [], TotalResults: 0, Note: "No matching article chunks found.");

        return new SemanticSearchResultDto(
            Query: query,
            Results: results
                .Select(r => new SemanticSearchHitDto(
                    PageId: r.PageId,
                    Title: r.Title,
                    Heading: r.Heading,
                    Type: r.Type,
                    Continuity: r.Continuity,
                    Score: Math.Round(r.Score, 4),
                    WikiUrl: r.WikiUrl,
                    SectionUrl: r.SectionUrl,
                    Text: Truncate(r.Text, 1500)
                ))
                .ToList(),
            TotalResults: results.Count
        );
    }

    [Description(
        """
            Get a complete snapshot of the galaxy at a specific year: territory control, events, and era context.
            Pre-computed — instant response. Use for 'What was happening in 19 BBY?'.
            """
    )]
    public async Task<GalaxyYearResultDto> GetGalaxyYear([Description("Year in sort-key format (-19 = 19 BBY, 4 = 4 ABY)")] int year)
    {
        var doc = await _galaxyYears.Find(d => d.Year == year).FirstOrDefaultAsync();
        if (doc is null)
        {
            var nearest = await _galaxyYearsRaw.Find(Builders<BsonDocument>.Filter.Ne(MongoFields.Id, "overview")).SortBy(d => d[MongoFields.Id]).ToListAsync();
            var available = nearest.Select(d => d[MongoFields.Id].AsInt32).OrderBy(y => Math.Abs(y - year)).Take(5).ToList();
            return new GalaxyYearResultDto(null, null, null, null, null, null, null, Error: $"No data for year {year}.", NearestYears: available);
        }

        return new GalaxyYearResultDto(
            Year: doc.Year,
            YearDisplay: doc.YearDisplay,
            Era: doc.Era,
            EraDescription: doc.EraDescription,
            TerritoryControl: doc.Regions.Select(r => new GalaxyRegionDto(
                    Region: r.Region,
                    Factions: r.Factions.Select(f => new GalaxyFactionDto(f.Faction, $"{f.Control * 100:0}%", f.Contested)).ToList()
                ))
                .ToList(),
            EventsOnMap: doc.EventCells.SelectMany(c => c.Events).Select(e => new GalaxyEventDto(e.Title, e.Lens, e.Place, e.Outcome, e.WikiUrl, e.Continuity.ToString())).Take(30).ToList(),
            TotalEvents: doc.EventCells.Sum(c => c.Count) + (doc.UnresolvedEvents?.Count ?? 0)
        );
    }

    [Description(
        """
            Get the full temporal lifecycle of an entity with rich semantic facets.
            Returns all temporal data points: birth/death for characters,
            established/dissolved/reorganized/restored/fragmented for institutions,
            beginning/end for conflicts, constructed/destroyed for structures, release dates for publications.
            Each facet includes the semantic dimension, calendar system, parsed year, and original text.
            Use for 'When was the Galactic Republic reorganized?', 'When did Yoda die?'.
            """
    )]
    public async Task<EntityTimelineDto> GetEntityTimeline([Description("The PageId of the entity")] int entityId)
    {
        var node = await _kg.GetNodeByIdAsync(entityId);
        if (node is null)
            return new EntityTimelineDto(null, null, null, null, null, null, null, null, null, null, Error: "Entity not found.");

        return new EntityTimelineDto(
            PageId: node.PageId,
            Name: node.Name,
            Type: node.Type,
            Continuity: node.Continuity.ToString(),
            Realm: node.Realm.ToString(),
            StartYear: node.StartYear,
            EndYear: node.EndYear,
            Duration: node.StartYear.HasValue && node.EndYear.HasValue ? $"{Math.Abs(node.EndYear.Value - node.StartYear.Value)} years" : null,
            WikiUrl: node.WikiUrl,
            TemporalFacets: node.TemporalFacets.OrderBy(f => f.Order).Select(f => ToFacetDto(f, includeOrder: true)).ToList()
        );
    }

    [Description(
        """
            List all entity types available in the knowledge graph with counts.
            Use to discover valid type values for find_entities_by_year and search_entities.
            Returns types sorted by count descending.
            """
    )]
    public async Task<List<TypeCountDto>> ListEntityTypes([Description("Only include types that have temporal facets. Default true.")] bool temporalOnly = true)
    {
        var matchStage = temporalOnly ? new BsonDocument("$match", new BsonDocument("temporalFacets.0", new BsonDocument("$exists", true))) : new BsonDocument("$match", new BsonDocument());

        var pipeline = new[]
        {
            matchStage,
            new BsonDocument("$group", new BsonDocument { { MongoFields.Id, "$" + GraphNodeBsonFields.Type }, { "count", new BsonDocument("$sum", 1) } }),
            new BsonDocument("$sort", new BsonDocument("count", -1)),
        };

        var results = await _galaxyYearsRaw.Database.GetCollection<BsonDocument>(Collections.KgNodes).AggregateAsync<BsonDocument>(pipeline);
        var docs = await results.ToListAsync();

        return docs.Select(d => new TypeCountDto(d[MongoFields.Id].AsString, d["count"].AsInt32)).ToList();
    }

    [Description(
        """
            List all relationship labels in the knowledge graph with usage counts.
            Helps choose label filters for get_entity_relationships and traverse_graph.
            """
    )]
    public async Task<List<LabelCountDto>> ListRelationshipLabels([Description("Max results (default 50)")] int limit = 50)
    {
        var pipeline = new[]
        {
            new BsonDocument("$group", new BsonDocument { { MongoFields.Id, "$" + RelationshipEdgeBsonFields.Label }, { "count", new BsonDocument("$sum", 1) } }),
            new BsonDocument("$sort", new BsonDocument("count", -1)),
            new BsonDocument("$limit", limit),
        };

        var results = await _galaxyYearsRaw.Database.GetCollection<BsonDocument>(Collections.KgEdges).AggregateAsync<BsonDocument>(pipeline);
        var docs = await results.ToListAsync();

        return docs.Select(d => new LabelCountDto(d[MongoFields.Id].AsString, d["count"].AsInt32)).ToList();
    }

    public IReadOnlyList<AITool> AsAIFunctions() =>
        [
            AIFunctionFactory.Create(SearchEntities, "search_entities"),
            AIFunctionFactory.Create(FindEntitiesByYear, "find_entities_by_year"),
            AIFunctionFactory.Create(GetEntityProperties, "get_entity_properties"),
            AIFunctionFactory.Create(GetEntityTimeline, "get_entity_timeline"),
            AIFunctionFactory.Create(GetEntityRelationships, "get_entity_relationships"),
            AIFunctionFactory.Create(GetRelationshipTypes, "get_relationship_types"),
            AIFunctionFactory.Create(TraverseGraph, "traverse_graph"),
            AIFunctionFactory.Create(GetLineage, "get_lineage"),
            AIFunctionFactory.Create(FindConnections, "find_connections"),
            AIFunctionFactory.Create(GetGalaxyYear, "get_galaxy_year"),
            AIFunctionFactory.Create(ListEntityTypes, "list_entity_types"),
            AIFunctionFactory.Create(ListRelationshipLabels, "list_relationship_labels"),
            AIFunctionFactory.Create(SemanticSearch, "semantic_search"),
        ];

    static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? ""
        : s.Length > max ? s[..(max - 1)] + "\u2026"
        : s;
}
