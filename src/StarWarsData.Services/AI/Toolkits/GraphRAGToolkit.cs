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
    readonly IMongoCollection<GraphNode> _nodesCollection;
    readonly IMongoCollection<GalaxyYearDocument> _galaxyYears;
    readonly IMongoCollection<BsonDocument> _galaxyYearsRaw;

    const string ContinuityParamDescription = "Optional continuity filter: Canon, Legends, or omit for all";

    public GraphRAGToolkit(KnowledgeGraphQueryService kg, SemanticSearchService search, IMongoClient mongoClient, string databaseName)
    {
        _kg = kg;
        _search = search;
        var db = mongoClient.GetDatabase(databaseName);
        _nodesCollection = db.GetCollection<GraphNode>(Collections.KgNodes);
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
            PRIMARY ENTRY POINT for ANY entity lookup. Start here before any Pages-side search.
            This is the default front door for every question that names an entity or asks
            about one — "Tell me about X", "X and Y", "What is X", "who is X", etc.

            Search the knowledge graph for entities by name. Returns entities with their type,
            properties, and temporal lifecycle. Use to resolve entity names to PageIds before
            calling other graph tools.

            ROUTING:
              "Tell me about X" / "Compare X and Y" → search_entities → render_infobox
              "X's relationships / mentors / allies" (TEXT) → search_entities → get_entity_relationships
              "X's family tree / relationship graph / hierarchy / network" (VISUAL)
                → search_entities → get_relationship_types → render_graph
              "When did X happen / live" → search_entities → get_entity_timeline
              "X from Y" (food from Tatooine, battles of the Clone Wars, etc.)
                → search_entities(Y) → get_entity_relationships with reverse edges

            ANTI-PATTERN: Do not call this tool to "discover" obvious facts. If the user named
            entities directly, look up the names you already have. Never call this tool more than
            once per distinct entity in a single turn.
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

            Typical render follow-up:
              - year/range lists → render_data_table with names, types, and years
              - short prose summary → render_markdown
            Do NOT stop after this tool and answer in plain text — always finish with a render_* tool.

            Examples:
              'Characters alive in 19 BBY' → year=-19, type='Character', semantic='lifespan' → render_data_table
              'Wars during 4000-1000 BBY'  → year=-4000, yearEnd=-1000, type='War', semantic='conflict' → render_data_table
              'Books published in 2015'    → year=2015, type='Book', semantic='publication' → render_data_table
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
            Get properties (attributes) of one or more knowledge graph entities — height, eye color, classification, etc.
            Accepts a single PageId or a comma-separated list of PageIds for batch lookups (max 20).
            Use for factual questions about entity characteristics. Call search_entities first to get PageIds.

            CRITICAL: ALWAYS batch every PageId you need into ONE call. Calling this tool in a
            loop, once per entity, is the most expensive mistake the agent can make and is
            ALWAYS wrong. If you need 8 character profiles, that is ONE call with 8 IDs, not 8
            calls with 1 ID each.
            """
    )]
    public async Task<List<KgNodeDetailDto>> GetEntityProperties([Description("Comma-separated PageIds (e.g. '12345' or '12345,67890,11111'). Max 20.")] string entityIds)
    {
        var ids = entityIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var id) ? id : (int?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Take(20)
            .ToList();

        if (ids.Count == 0)
            return [new KgNodeDetailDto(null, null, null, null, null, null, null, null, null, null, Error: "No valid PageIds provided.")];

        var filter = Builders<GraphNode>.Filter.In(n => n.PageId, ids);
        var nodes = await _nodesCollection.Find(filter).ToListAsync();

        if (nodes.Count == 0)
            return [new KgNodeDetailDto(null, null, null, null, null, null, null, null, null, null, Error: "No entities found in knowledge graph.")];

        return nodes
            .Select(node => new KgNodeDetailDto(
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
            ))
            .ToList();
    }

    [Description(
        """
            PRIMARY TOOL for "X from Y", "who connects to Z", org / alliance queries,
            cross-reference lookup, and any question asking for entities related to a named
            entity. This is the front door for reverse-lookup questions — it walks BIDIRECTIONAL
            edges so you query from Y and get every X that points at it, even when Y's own page
            never enumerates its consumers. Prefer this over search_pages_by_property and
            search_pages_by_link for all relationship questions.

            NOT FOR VISUAL GRAPHS. If the question implies a visual output — "family tree",
            "relationship graph", "hierarchy", "network", "show me X's connections" — do NOT
            use this tool. Use the render_graph workflow instead:
              search_entities → get_relationship_types → render_graph.
            This tool returns raw data for TEXT/TABLE answers about relationships.

            CRITICAL — DISCOVER ALL RELEVANT LABELS BEFORE FILTERING. The same concept is often
            represented by MULTIPLE edge labels. "Food from Tatooine" lives under both
            `found_at` (71 edges) AND `originates_from` (18 edges) — picking just one of the two
            misses most of the data. ALWAYS either:
              (a) Call get_relationship_types(entityId) first to see all available labels for the
                  root, then pass the relevant ones as a comma-separated labelFilter, OR
              (b) Call this with NO labelFilter to get ALL incoming and outgoing edges grouped by
                  label, then read the result to discover which labels matter and (only if needed)
                  re-call with a filter.

            Examples of the reverse-lookup power:
              "food and drink from Tatooine"
                → search_entities("Tatooine") → get_relationship_types(tatooineId)
                → get_entity_relationships(tatooineId, labelFilter="found_at,originates_from")
                  (returns ALL Foods/Plants linked to Tatooine via either label, ~80 items)
              "who trained under Yoda"
                → search_entities("Yoda") → get_entity_relationships(yodaId,
                  labelFilter="master_of,trained")  (apprentice labels — discover variants first)
              "battles commanded by Anakin"
                → search_entities("Anakin") → get_entity_relationships(anakinId,
                  labelFilter="commanded,fought_in")  (multiple military-role labels)

            ANTI-PATTERN: do NOT pick a single label from prior knowledge. Edge labels in this KG
            are normalized from infobox field names and the same semantic concept may have several
            variants. When in doubt, no filter beats a wrong filter.

            Get all direct relationships for an entity, grouped by relationship label.
            BIDIRECTIONAL: returns both outgoing edges AND inbound edges rewritten with their
            reverse label so they read from the entity's perspective. For example, battles
            commanded by Anakin (stored as Battle → commanded_by → Anakin) appear on Anakin as
            'commanded → Battle'.
            Label filter accepts either the forward (commanded_by) or reverse (commanded) form.
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
                EntityWikiUrl: null,
                Relationships: new Dictionary<string, List<RelationshipTargetDto>>(),
                TotalEdges: 0,
                Note: "No relationships found (outgoing or inbound). The entity may not have been processed, or the label filter excluded everything."
            );

        // Batch-load target node enrichment (wikiUrl + property summary) so the agent has everything
        // it needs to cite each related entity without N follow-up calls. ALSO pull the root entity's
        // wikiUrl in the same batch so the caller can cite the root without re-querying search_entities.
        var targetIds = edges.Select(e => e.ToId).Distinct().ToList();
        if (!targetIds.Contains(entityId))
            targetIds.Add(entityId);
        var enrichment = await _kg.GetNodePropertiesBatchAsync(targetIds);
        enrichment.TryGetValue(entityId, out var rootInfo);

        var grouped = edges
            .GroupBy(e => e.Label)
            .ToDictionary(
                g => g.Key,
                g =>
                    g.Select(e =>
                        {
                            enrichment.TryGetValue(e.ToId, out var info);
                            return new RelationshipTargetDto(
                                PageId: e.ToId,
                                Name: e.ToName,
                                Type: e.ToType,
                                Weight: Math.Round(e.Weight, 2),
                                Evidence: Truncate(e.Evidence, 200),
                                WikiUrl: info?.WikiUrl,
                                Properties: info?.Properties
                            );
                        })
                        .ToList()
            );

        return new EntityRelationshipsDto(EntityId: entityId, EntityName: edges[0].FromName, EntityWikiUrl: rootInfo?.WikiUrl, Relationships: grouped, TotalEdges: edges.Count);
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
            ordered by depth. Use for TEXTUAL lineage questions like 'Yoda's master chain',
            'successors of the Galactic Republic', 'Palpatine's apprentices down the line'.

            NOT FOR VISUAL FAMILY TREES. If the question asks for a "family tree", "ancestry
            graph", or visual hierarchy, use render_graph(layoutMode=Tree) instead — it renders
            an interactive D3 visualization. This tool returns a text chain only.

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

        return new ConnectionsDto(
            Connected: true,
            PathLength: path.Count,
            Path: path.Select(s => new ConnectionStepDto(s.from, s.fromName, s.fromType, s.to, s.toName, s.toType, s.label, Truncate(s.evidence, 200))).ToList()
        );
    }

    [Description(
        """
            Semantic search over 800K+ wiki article passages using AI embeddings.
            Finds content by MEANING, not keywords — understands concepts, paraphrases, and indirect references.
            Returns the most relevant passages with title, wikiUrl, sectionUrl (deep link), and text.

            ROUTING:
              "Why did X happen?" / "What caused Y?" / "Explain X" → semantic_search → render_markdown
              "What was X's philosophy / motivation?" → semantic_search → render_markdown
              "Aftermath / consequences / legacy of X" → semantic_search → render_markdown
              For exact name lookups, use search_entities or keyword_search instead.

            DO NOT use this for temporal fact queries such as:
              - "Who was alive during the Clone Wars?"
              - "What governments existed in 19 BBY?"
              - "Tell me about the rise and fall of the Galactic Republic"
              - "What Star Wars books were published in 2015?"
            Those belong to find_entities_by_year or get_entity_timeline, then a render_* tool.

            CRITICAL — DO NOT FAN OUT. Issue ONE broad query, not many narrow ones.
            "Strategic mistakes at Endor" is a SINGLE search, not eight searches for "shield generator",
            "Death Star II", "Bothan spies", "Ewoks", "overconfidence" etc. Embeddings find concepts
            from a single phrase — you do not need to enumerate sub-topics. If the first call returns
            relevant snippets, SYNTHESIZE the answer; do not search again with reworded variants.
            Cap yourself at TWO semantic_search calls per question; if you cannot answer after two,
            you have enough material — write the answer.
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
        Continuity? cont = continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var c) && c is Continuity.Canon or Continuity.Legends ? c : null;

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
            Follow with render_markdown or render_timeline — do not reply with plain text alone.
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
            Get the full temporal lifecycle of one or more entities with rich semantic facets.
            Accepts a single PageId or comma-separated PageIds (max 10).
            Returns all temporal data points: birth/death for characters,
            established/dissolved/reorganized/restored/fragmented for institutions,
            beginning/end for conflicts, constructed/destroyed for structures, release dates for publications.
            Each facet includes the semantic dimension, calendar system, parsed year, and original text.
            ALWAYS pass multiple IDs in one call rather than calling this function multiple times.

            This is the PRIMARY tool for lifecycle questions like "rise and fall", "when was X reorganized",
            or "what happened to Y over time". The correct pattern is:
              search_entities → get_entity_timeline (ONE batched call) → render_markdown with the
              lifecycle steps in chronological order.
            Do NOT start with semantic_search for these questions, and do NOT stop at plain text after this call.
            Use for 'When was the Galactic Republic reorganized?', 'When did Yoda die?'.
            """
    )]
    public async Task<List<EntityTimelineDto>> GetEntityTimeline([Description("Comma-separated PageIds (e.g. '12345' or '12345,67890'). Max 10.")] string entityIds)
    {
        var ids = entityIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var id) ? id : (int?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Take(10)
            .ToList();

        if (ids.Count == 0)
            return [new EntityTimelineDto(null, null, null, null, null, null, null, null, null, null, Error: "No valid PageIds provided.")];

        var filter = Builders<GraphNode>.Filter.In(n => n.PageId, ids);
        var nodes = await _nodesCollection.Find(filter).ToListAsync();

        if (nodes.Count == 0)
            return [new EntityTimelineDto(null, null, null, null, null, null, null, null, null, null, Error: "No entities found.")];

        return nodes
            .Select(node => new EntityTimelineDto(
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
            ))
            .ToList();
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
            AIFunctionFactory.Create(SearchEntities, ToolNames.GraphRAG.SearchEntities),
            AIFunctionFactory.Create(FindEntitiesByYear, ToolNames.GraphRAG.FindEntitiesByYear),
            AIFunctionFactory.Create(GetEntityProperties, ToolNames.GraphRAG.GetEntityProperties),
            AIFunctionFactory.Create(GetEntityTimeline, ToolNames.GraphRAG.GetEntityTimeline),
            AIFunctionFactory.Create(GetEntityRelationships, ToolNames.GraphRAG.GetEntityRelationships),
            AIFunctionFactory.Create(GetRelationshipTypes, ToolNames.GraphRAG.GetRelationshipTypes),
            AIFunctionFactory.Create(TraverseGraph, ToolNames.GraphRAG.TraverseGraph),
            AIFunctionFactory.Create(GetLineage, ToolNames.GraphRAG.GetLineage),
            AIFunctionFactory.Create(FindConnections, ToolNames.GraphRAG.FindConnections),
            AIFunctionFactory.Create(GetGalaxyYear, ToolNames.GraphRAG.GetGalaxyYear),
            AIFunctionFactory.Create(ListEntityTypes, ToolNames.GraphRAG.ListEntityTypes),
            AIFunctionFactory.Create(ListRelationshipLabels, ToolNames.GraphRAG.ListRelationshipLabels),
            AIFunctionFactory.Create(SemanticSearch, ToolNames.GraphRAG.SemanticSearch),
        ];

    static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? ""
        : s.Length > max ? s[..(max - 1)] + "\u2026"
        : s;
}
