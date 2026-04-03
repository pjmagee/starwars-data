using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services;

/// <summary>
/// AI tools for the Ask AI agent — thin wrapper around KnowledgeGraphQueryService.
/// Each method is an AITool that queries the KG and returns JSON for LLM consumption.
/// Non-KG tools (vector search, galaxy year) query their own collections directly.
/// </summary>
public class GraphRAGToolkit
{
    readonly KnowledgeGraphQueryService _kg;
    readonly IMongoCollection<BsonDocument> _chunksRaw;
    readonly IMongoCollection<GalaxyYearDocument> _galaxyYears;
    readonly IMongoCollection<BsonDocument> _galaxyYearsRaw;
    readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;

    public GraphRAGToolkit(
        KnowledgeGraphQueryService kg,
        IMongoClient mongoClient,
        string databaseName,
        IEmbeddingGenerator<string, Embedding<float>> embedder
    )
    {
        _kg = kg;
        var db = mongoClient.GetDatabase(databaseName);
        _chunksRaw = db.GetCollection<BsonDocument>(Collections.SearchChunks);
        _galaxyYears = db.GetCollection<GalaxyYearDocument>(Collections.GalaxyYears);
        _galaxyYearsRaw = db.GetCollection<BsonDocument>(Collections.GalaxyYears);
        _embedder = embedder;
    }

    [Description(
        "Search the knowledge graph for entities by name. Returns entities with their type, "
            + "properties, and temporal lifecycle. Use this to resolve entity names to PageIds before "
            + "calling other graph tools."
    )]
    public async Task<string> SearchEntities(
        [Description("Entity name to search for (case-insensitive partial match)")] string query,
        [Description("Optional entity type filter (e.g. Character, Organization, CelestialBody)")]
            string? type = null,
        [Description("Optional continuity filter: Canon, Legends, or omit for all")]
            string? continuity = null,
        [Description("Max results to return (default 10)")] int limit = 10
    )
    {
        var results = await _kg.SearchNodesAsync(query, type, continuity, Math.Min(limit, 20));
        return JsonSerializer.Serialize(
            results.Select(n => new
            {
                pageId = n.PageId,
                name = n.Name,
                type = n.Type,
                continuity = n.Continuity.ToString(),
                imageUrl = n.ImageUrl,
                wikiUrl = n.WikiUrl,
                startYear = n.StartYear,
                endYear = n.EndYear,
                temporalFacets = n.TemporalFacets.Select(f => new
                {
                    f.Field,
                    f.Semantic,
                    f.Calendar,
                    f.Year,
                    f.Text,
                }),
            })
        );
    }

    [Description(
        "Find entities in the temporal knowledge graph that existed during a year or year range. "
            + "Pass a single year OR a range (year + yearEnd) to find everything active in that window. "
            + "Use the optional 'semantic' parameter to filter by temporal dimension: "
            + "'lifespan' = who was alive, 'conflict' = battles/wars/missions happening, "
            + "'institutional' = governments/organizations existing, 'construction' = structures/ships built, "
            + "'creation' = artifacts/devices existing, 'publication' = media released (real-world CE years). "
            + "Examples: 'Characters alive in 19 BBY' → year=-19, type='Character', semantic='lifespan'. "
            + "'Wars during 4000-1000 BBY' → year=-4000, yearEnd=-1000, type='War', semantic='conflict'. "
            + "'Books published in 2015' → year=2015, type='Book', semantic='publication'. "
            + "Years use sort-key format: negative = BBY, positive = ABY for galactic dates, "
            + "or CE year (e.g. 2015) for publication dates."
    )]
    public async Task<string> FindEntitiesByYear(
        [Description(
            "Start year or single year (sort-key: -19 = 19 BBY, 4 = 4 ABY, or CE year like 2015 for publications)"
        )]
            int year,
        [Description(
            "Entity type filter (e.g. Character, Government, Organization, Battle, War, Book)"
        )]
            string type,
        [Description("Optional end year for range queries. Omit to query a single year.")]
            int? yearEnd = null,
        [Description("Optional continuity filter: Canon, Legends, or omit for all")]
            string? continuity = null,
        [Description(
            "Optional temporal dimension: lifespan, conflict, institutional, construction, creation, publication. "
                + "Filters on temporalFacets.semantic prefix. Omit to use the flat startYear/endYear envelope."
        )]
            string? semantic = null,
        [Description("Max results (default 20)")] int limit = 20
    )
    {
        var results = await _kg.FindNodesByYearAsync(
            year,
            type,
            yearEnd,
            continuity,
            semantic,
            limit
        );
        return JsonSerializer.Serialize(
            results.Select(n => new
            {
                pageId = n.PageId,
                name = n.Name,
                type = n.Type,
                continuity = n.Continuity.ToString(),
                startYear = n.StartYear,
                endYear = n.EndYear,
                temporalFacets = n.TemporalFacets.Select(f => new
                {
                    f.Field,
                    f.Semantic,
                    f.Calendar,
                    f.Year,
                    f.Text,
                }),
                imageUrl = n.ImageUrl,
                wikiUrl = n.WikiUrl,
            })
        );
    }

    [Description(
        "Get properties (attributes) of a knowledge graph entity — height, eye color, classification, etc. "
            + "Use for factual questions about an entity's characteristics. Call search_entities first to get the PageId."
    )]
    public async Task<string> GetEntityProperties(
        [Description("The PageId of the entity (from search_entities)")] int entityId
    )
    {
        var node = await _kg.GetNodeByIdAsync(entityId);
        if (node is null)
            return JsonSerializer.Serialize(new { error = "Entity not found in knowledge graph." });

        return JsonSerializer.Serialize(
            new
            {
                pageId = node.PageId,
                name = node.Name,
                type = node.Type,
                continuity = node.Continuity.ToString(),
                properties = node.Properties,
                startYear = node.StartYear,
                endYear = node.EndYear,
                temporalFacets = node.TemporalFacets.Select(f => new
                {
                    f.Field,
                    f.Semantic,
                    f.Calendar,
                    f.Year,
                    f.Text,
                    f.Order,
                }),
                imageUrl = node.ImageUrl,
                wikiUrl = node.WikiUrl,
            }
        );
    }

    [Description(
        "Get all direct relationships for an entity, grouped by relationship label. "
            + "Returns relationship types with their connected entities and supporting evidence. "
            + "Use this to answer questions like 'Who trained X?', 'What are X's relationships?', "
            + "'Who are X's allies?'. Call search_entities first to get the entity's PageId."
    )]
    public async Task<string> GetEntityRelationships(
        [Description("The PageId of the entity (from search_entities)")] int entityId,
        [Description(
            "Optional comma-separated labels to filter (e.g. 'parent_of,trained_by'). Omit for all."
        )]
            string? labelFilter = null,
        [Description("Optional continuity filter: Canon, Legends, or omit for all")]
            string? continuity = null,
        [Description("Max edges to return (default 30)")] int limit = 30
    )
    {
        var edges = await _kg.GetEdgesFromEntityAsync(entityId, labelFilter, continuity, limit);

        if (edges.Count == 0)
            return JsonSerializer.Serialize(
                new
                {
                    entityId,
                    relationships = new { },
                    totalEdges = 0,
                    note = "No relationships found. The entity may not have been processed yet.",
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
                            weight = Math.Round(e.Weight, 2),
                            evidence = Truncate(e.Evidence, 200),
                        })
                        .ToList()
            );

        return JsonSerializer.Serialize(
            new
            {
                entityId,
                entityName = edges.First().FromName,
                relationships = grouped,
                totalEdges = edges.Count,
            }
        );
    }

    [Description(
        "List what types of relationships an entity has in the knowledge graph. "
            + "Returns each relationship label with its count and description. "
            + "Use this to discover what relationship types are available before filtering with get_entity_relationships."
    )]
    public async Task<string> GetRelationshipTypes(
        [Description("The PageId of the entity (from search_entities)")] int entityId,
        [Description("Optional continuity filter: Canon, Legends, or omit for all")]
            string? continuity = null
    )
    {
        var results = await _kg.GetRelationshipTypesAsync(entityId, continuity);
        return JsonSerializer.Serialize(
            results.Select(r => new
            {
                label = r.label,
                count = r.count,
                avgWeight = Math.Round(r.avgWeight, 2),
            })
        );
    }

    [Description(
        "Traverse the relationship graph from an entity up to N hops deep. "
            + "Returns all connected entities and their relationships as context for answering questions. "
            + "Use for broad exploration like 'Show me everyone connected to Yoda' or 'What is Palpatine's network?'. "
            + "For simple direct relationships, prefer get_entity_relationships instead."
    )]
    public async Task<string> TraverseGraph(
        [Description("The PageId of the starting entity (from search_entities)")] int entityId,
        [Description(
            "Optional comma-separated labels to follow (e.g. 'trained_by,trained'). Omit for all."
        )]
            string? labels = null,
        [Description("Max traversal depth, 1-3 (default 2). Higher values return more data.")]
            int maxDepth = 2,
        [Description("Optional continuity filter: Canon, Legends, or omit for all")]
            string? continuity = null
    )
    {
        // Delegate to the service's BFS traversal (same as render_graph uses)
        var result = await _kg.QueryGraphAsync(entityId, labels, maxDepth, continuity, default);

        return JsonSerializer.Serialize(
            new
            {
                root = new { id = entityId },
                nodes = result.Nodes.Select(n => new
                {
                    pageId = n.Id,
                    name = n.Name,
                    type = n.Type,
                }),
                edges = result.Edges.Select(e => new
                {
                    from = e.FromId,
                    to = e.ToId,
                    label = e.Label,
                    weight = Math.Round(e.Weight, 2),
                }),
                summary = $"Found {result.Nodes.Count} connected entities and {result.Edges.Count} relationships across {maxDepth} hops",
            }
        );
    }

    [Description(
        "Find how two entities are connected in the knowledge graph. "
            + "Returns the shortest path between them with relationship labels and evidence. "
            + "Use for questions like 'How is Palpatine connected to Luke Skywalker?' or "
            + "'What is the relationship between the Jedi Order and the Republic?'. "
            + "Call search_entities first to get PageIds for both entities."
    )]
    public async Task<string> FindConnections(
        [Description("PageId of the first entity")] int entityId1,
        [Description("PageId of the second entity")] int entityId2,
        [Description("Maximum path length to search (default 3, max 4)")] int maxHops = 3,
        [Description("Optional continuity filter: Canon, Legends, or omit for all")]
            string? continuity = null
    )
    {
        if (entityId1 == entityId2)
            return JsonSerializer.Serialize(
                new
                {
                    connected = true,
                    pathLength = 0,
                    path = Array.Empty<object>(),
                    note = "Same entity",
                }
            );

        var (connected, path) = await _kg.FindConnectionsAsync(
            entityId1,
            entityId2,
            maxHops,
            continuity
        );

        if (!connected)
            return JsonSerializer.Serialize(
                new
                {
                    connected = false,
                    searchedHops = maxHops,
                    note = $"No connection found within {maxHops} hops.",
                }
            );

        return JsonSerializer.Serialize(
            new
            {
                connected = true,
                pathLength = path.Count,
                path = path.Select(s => new
                {
                    from = s.fromName,
                    to = s.toName,
                    label = s.label,
                    evidence = Truncate(s.evidence, 200),
                }),
            }
        );
    }

    [Description(
        "Search article content using semantic vector search over chunked wiki pages. "
            + "Returns the most relevant article passages based on meaning, not just keyword matching. "
            + "Each result includes title, wikiUrl, sectionUrl (deep link to the article section), and text — use title + sectionUrl as references. "
            + "Use this for lore questions, historical context, event details, or any question where "
            + "the answer is in article body text rather than structured infobox data. "
            + "Combine with graph tools for comprehensive GraphRAG answers."
    )]
    public async Task<string> SearchArticleContent(
        [Description(
            "Natural language search query (e.g. 'Battle of Endor aftermath', 'Darth Vader's redemption')"
        )]
            string query,
        [Description(
            "Optional entity type filter (e.g. Character, Planet, Battle). Omit for all types."
        )]
            string? type = null,
        [Description("Optional continuity filter: Canon, Legends, or omit for all")]
            string? continuity = null,
        [Description("Max results to return (default 5, max 10)")] int limit = 5
    )
    {
        limit = Math.Clamp(limit, 1, 10);

        var embeddings = await _embedder.GenerateAsync([query]);
        var queryVector = embeddings[0].Vector.ToArray();

        var vectorSearchStage = new BsonDocument(
            "$vectorSearch",
            new BsonDocument
            {
                { "index", "chunks_vector_index" },
                { "path", "embedding" },
                { "queryVector", new BsonArray(queryVector.Select(f => (double)f)) },
                { "numCandidates", limit * 20 },
                { "limit", limit },
            }
        );

        var filter = new BsonDocument();
        if (!string.IsNullOrWhiteSpace(type))
            filter["type"] = type;
        if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont))
            filter["continuity"] = cont.ToString();
        if (filter.ElementCount > 0)
            vectorSearchStage["$vectorSearch"]["filter"] = filter;

        var projectStage = new BsonDocument(
            "$project",
            new BsonDocument
            {
                { "_id", 0 },
                { "pageId", 1 },
                { "title", 1 },
                { "heading", 1 },
                { "section", 1 },
                { "wikiUrl", 1 },
                { "text", 1 },
                { "type", 1 },
                { "continuity", 1 },
                { "score", new BsonDocument("$meta", "vectorSearchScore") },
            }
        );

        var results = await _chunksRaw
            .Aggregate<BsonDocument>(new BsonDocument[] { vectorSearchStage, projectStage })
            .ToListAsync();

        if (results.Count == 0)
            return JsonSerializer.Serialize(
                new
                {
                    query,
                    results = Array.Empty<object>(),
                    note = "No matching article chunks found.",
                }
            );

        return JsonSerializer.Serialize(
            new
            {
                query,
                results = results.Select(d =>
                {
                    var wikiUrl =
                        d.Contains("wikiUrl") && !d["wikiUrl"].IsBsonNull
                            ? d["wikiUrl"].AsString
                            : "";
                    var section =
                        d.Contains("section") && !d["section"].IsBsonNull
                            ? d["section"].AsString
                            : "";
                    return new
                    {
                        pageId = d["pageId"].AsInt32,
                        title = d["title"].AsString,
                        heading = d.Contains("heading") ? d["heading"].AsString : "",
                        type = d.Contains("type") ? d["type"].AsString : "",
                        continuity = d.Contains("continuity") ? d["continuity"].AsString : "",
                        score = d.Contains("score") ? Math.Round(d["score"].AsDouble, 4) : 0,
                        wikiUrl,
                        sectionUrl = !string.IsNullOrEmpty(wikiUrl)
                        && !string.IsNullOrEmpty(section)
                            ? $"{wikiUrl}#{section}"
                            : wikiUrl,
                        text = Truncate(d["text"].AsString, 1500),
                    };
                }),
                totalResults = results.Count,
            }
        );
    }

    [Description(
        "Get a complete snapshot of the galaxy at a specific year: territory control, events, and era context. "
            + "Pre-computed data — instant response. Use for questions like 'What was happening in 19 BBY?'. "
            + "Years use sort-key: negative = BBY, positive = ABY."
    )]
    public async Task<string> GetGalaxyYear(
        [Description("Year in sort-key format (-19 = 19 BBY, 4 = 4 ABY)")] int year
    )
    {
        var doc = await _galaxyYears.Find(d => d.Year == year).FirstOrDefaultAsync();
        if (doc is null)
        {
            var nearest = await _galaxyYearsRaw
                .Find(Builders<BsonDocument>.Filter.Ne("_id", "overview"))
                .SortBy(d => d["_id"])
                .ToListAsync();
            var available = nearest
                .Select(d => d["_id"].AsInt32)
                .OrderBy(y => Math.Abs(y - year))
                .Take(5);
            return JsonSerializer.Serialize(
                new { error = $"No data for year {year}.", nearestYears = available }
            );
        }

        return JsonSerializer.Serialize(
            new
            {
                year = doc.Year,
                yearDisplay = doc.YearDisplay,
                era = doc.Era,
                eraDescription = doc.EraDescription,
                territoryControl = doc.Regions.Select(r => new
                {
                    region = r.Region,
                    factions = r.Factions.Select(f => new
                    {
                        f.Faction,
                        control = $"{f.Control * 100:0}%",
                        f.Contested,
                    }),
                }),
                eventsOnMap = doc
                    .EventCells.SelectMany(c => c.Events)
                    .Select(e => new
                    {
                        e.Title,
                        e.Lens,
                        e.Place,
                        e.Outcome,
                        e.WikiUrl,
                        continuity = e.Continuity.ToString(),
                    })
                    .Take(30),
                totalEvents = doc.EventCells.Sum(c => c.Count) + (doc.UnresolvedEvents?.Count ?? 0),
            }
        );
    }

    [Description(
        "Get the full temporal lifecycle of an entity with rich semantic facets. "
            + "Returns all temporal data points: birth/death for characters, established/dissolved/reorganized/restored/fragmented "
            + "for institutions, beginning/end for conflicts, constructed/destroyed for structures, release dates for publications. "
            + "Each facet includes the semantic dimension, calendar system, parsed year, and original text. "
            + "Use for questions like 'When was the Galactic Republic reorganized?', 'When did Yoda die?'. "
            + "Call search_entities first to get the PageId."
    )]
    public async Task<string> GetEntityTimeline(
        [Description("The PageId of the entity")] int entityId
    )
    {
        var node = await _kg.GetNodeByIdAsync(entityId);
        if (node is null)
            return JsonSerializer.Serialize(new { error = "Entity not found." });

        return JsonSerializer.Serialize(
            new
            {
                pageId = node.PageId,
                name = node.Name,
                type = node.Type,
                continuity = node.Continuity.ToString(),
                universe = node.Universe.ToString(),
                startYear = node.StartYear,
                endYear = node.EndYear,
                duration = node.StartYear.HasValue && node.EndYear.HasValue
                    ? $"{Math.Abs(node.EndYear.Value - node.StartYear.Value)} years"
                    : null,
                wikiUrl = node.WikiUrl,
                temporalFacets = node
                    .TemporalFacets.OrderBy(f => f.Order)
                    .Select(f => new
                    {
                        f.Field,
                        f.Semantic,
                        f.Calendar,
                        f.Year,
                        f.Text,
                        f.Order,
                    }),
            }
        );
    }

    [Description(
        "List all entity types available in the knowledge graph with counts. Use this to discover valid type values "
            + "for find_entities_by_year and search_entities. Returns types sorted by count descending."
    )]
    public async Task<string> ListEntityTypes(
        [Description("Only include types that have temporal facets. Default true.")]
            bool temporalOnly = true
    )
    {
        // This uses a specific aggregation not in the service — keep inline
        var matchStage = temporalOnly
            ? new BsonDocument(
                "$match",
                new BsonDocument("temporalFacets.0", new BsonDocument("$exists", true))
            )
            : new BsonDocument("$match", new BsonDocument());

        var pipeline = new[]
        {
            matchStage,
            new BsonDocument(
                "$group",
                new BsonDocument { { "_id", "$type" }, { "count", new BsonDocument("$sum", 1) } }
            ),
            new BsonDocument("$sort", new BsonDocument("count", -1)),
        };

        // Access the raw collection via the service's mongo client
        var results = await _chunksRaw
            .Database.GetCollection<BsonDocument>(Collections.KgNodes)
            .AggregateAsync<BsonDocument>(pipeline);
        var docs = await results.ToListAsync();

        return JsonSerializer.Serialize(
            docs.Select(d => new { type = d["_id"].AsString, count = d["count"].AsInt32 })
        );
    }

    [Description(
        "List all relationship labels in the knowledge graph with usage counts. Use this to discover what kinds of "
            + "relationships exist. Helps choose label filters for get_entity_relationships and traverse_graph."
    )]
    public async Task<string> ListRelationshipLabels(
        [Description("Max results (default 50)")] int limit = 50
    )
    {
        var pipeline = new[]
        {
            new BsonDocument(
                "$group",
                new BsonDocument { { "_id", "$label" }, { "count", new BsonDocument("$sum", 1) } }
            ),
            new BsonDocument("$sort", new BsonDocument("count", -1)),
            new BsonDocument("$limit", limit),
        };

        var results = await _chunksRaw
            .Database.GetCollection<BsonDocument>(Collections.KgEdges)
            .AggregateAsync<BsonDocument>(pipeline);
        var docs = await results.ToListAsync();

        return JsonSerializer.Serialize(
            docs.Select(d => new { label = d["_id"].AsString, count = d["count"].AsInt32 })
        );
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
            AIFunctionFactory.Create(FindConnections, "find_connections"),
            AIFunctionFactory.Create(GetGalaxyYear, "get_galaxy_year"),
            AIFunctionFactory.Create(ListEntityTypes, "list_entity_types"),
            AIFunctionFactory.Create(ListRelationshipLabels, "list_relationship_labels"),
            AIFunctionFactory.Create(SearchArticleContent, "search_article_content"),
        ];

    static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? ""
        : s.Length > max ? s[..(max - 1)] + "\u2026"
        : s;
}
