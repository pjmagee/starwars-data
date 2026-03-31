using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services;

/// <summary>
/// AI tools that give the Ask AI agent read-only access to the persistent
/// relationship knowledge graph and article chunk vector search. Enables
/// GraphRAG-style answers: the agent can resolve entity names, traverse
/// relationships, find connections between entities, and search article
/// content via semantic vector search.
/// </summary>
public class GraphRAGToolkit
{
    readonly IMongoCollection<RelationshipEdge> _edges;
    readonly IMongoCollection<GraphNode> _nodes;
    readonly IMongoCollection<BsonDocument> _chunksRaw;
    readonly IMongoCollection<GalaxyYearDocument> _galaxyYears;
    readonly IMongoCollection<BsonDocument> _galaxyYearsRaw;
    readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;

    public GraphRAGToolkit(
        IMongoClient mongoClient,
        string databaseName,
        IEmbeddingGenerator<string, Embedding<float>> embedder
    )
    {
        var db = mongoClient.GetDatabase(databaseName);
        _edges = db.GetCollection<RelationshipEdge>(Collections.KgEdges);
        _nodes = db.GetCollection<GraphNode>(Collections.KgNodes);
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
    public async Task<string> SearchGraphEntities(
        [Description("Entity name to search for (case-insensitive partial match)")] string query,
        [Description("Optional entity type filter (e.g. Character, Organization, CelestialBody)")]
            string? type = null,
        [Description("Optional continuity filter: Canon, Legends, or omit for all")]
            string? continuity = null,
        [Description("Max results to return (default 10)")] int limit = 10
    )
    {
        var filters = new List<FilterDefinition<GraphNode>>
        {
            Builders<GraphNode>.Filter.Regex(
                n => n.Name,
                new BsonRegularExpression(query, "i")
            ),
        };

        if (!string.IsNullOrWhiteSpace(type))
            filters.Add(Builders<GraphNode>.Filter.Eq(n => n.Type, type));

        if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont))
            filters.Add(Builders<GraphNode>.Filter.Eq(n => n.Continuity, cont));

        var results = await _nodes
            .Find(Builders<GraphNode>.Filter.And(filters))
            .SortBy(n => n.Name)
            .Limit(Math.Min(limit, 20))
            .ToListAsync();

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
            })
        );
    }

    [Description(
        "Query the temporal knowledge graph to find entities that existed at a specific year or period. "
            + "Use for questions like 'What governments existed during 19 BBY?', 'Who was alive during the Clone Wars?', "
            + "'What organizations were active in 5 ABY?'. Years use sort-key format: negative = BBY, positive = ABY "
            + "(e.g. -19 = 19 BBY, 4 = 4 ABY)."
    )]
    public async Task<string> QueryEntitiesByYear(
        [Description("The year to query (sort-key: -19 = 19 BBY, 4 = 4 ABY)")] int year,
        [Description("Entity type filter (e.g. Character, Government, Organization, Battle, War)")] string type,
        [Description("Optional continuity filter: Canon, Legends, or omit for all")]
            string? continuity = null,
        [Description("Max results (default 20)")] int limit = 20
    )
    {
        var filters = new List<FilterDefinition<GraphNode>>
        {
            Builders<GraphNode>.Filter.Eq(n => n.Type, type),
            Builders<GraphNode>.Filter.Lte(n => n.StartYear, year),
            Builders<GraphNode>.Filter.Or(
                Builders<GraphNode>.Filter.Eq(n => n.EndYear, (int?)null),
                Builders<GraphNode>.Filter.Gte(n => n.EndYear, year)
            ),
        };

        if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont))
            filters.Add(Builders<GraphNode>.Filter.Eq(n => n.Continuity, cont));

        var results = await _nodes
            .Find(Builders<GraphNode>.Filter.And(filters))
            .SortBy(n => n.Name)
            .Limit(Math.Min(limit, 50))
            .ToListAsync();

        return JsonSerializer.Serialize(
            results.Select(n => new
            {
                pageId = n.PageId,
                name = n.Name,
                type = n.Type,
                continuity = n.Continuity.ToString(),
                startYear = n.StartYear,
                endYear = n.EndYear,
                startDateText = n.StartDateText,
                endDateText = n.EndDateText,
                imageUrl = n.ImageUrl,
                wikiUrl = n.WikiUrl,
            })
        );
    }

    [Description(
        "Get properties (attributes) of a knowledge graph entity — height, eye color, classification, etc. "
            + "Use for factual questions about an entity's characteristics. Call search_graph_entities first to get the PageId."
    )]
    public async Task<string> GetEntityProperties(
        [Description("The PageId of the entity (from search_graph_entities)")] int entityId
    )
    {
        var node = await _nodes.Find(n => n.PageId == entityId).FirstOrDefaultAsync();
        if (node is null)
            return JsonSerializer.Serialize(new { error = "Entity not found in knowledge graph." });

        return JsonSerializer.Serialize(new
        {
            pageId = node.PageId,
            name = node.Name,
            type = node.Type,
            continuity = node.Continuity.ToString(),
            properties = node.Properties,
            startYear = node.StartYear,
            endYear = node.EndYear,
            startDateText = node.StartDateText,
            endDateText = node.EndDateText,
            imageUrl = node.ImageUrl,
            wikiUrl = node.WikiUrl,
        });
    }

    [Description(
        "Get all direct relationships for an entity, grouped by relationship label. "
            + "Returns relationship types with their connected entities and supporting evidence. "
            + "Use this to answer questions like 'Who trained X?', 'What are X's relationships?', "
            + "'Who are X's allies?'. Call search_graph_entities first to get the entity's PageId."
    )]
    public async Task<string> GetEntityRelationships(
        [Description("The PageId of the entity (from search_graph_entities)")] int entityId,
        [Description(
            "Optional comma-separated labels to filter (e.g. 'parent_of,trained_by'). Omit for all."
        )]
            string? labelFilter = null,
        [Description("Optional continuity filter: Canon, Legends, or omit for all")]
            string? continuity = null,
        [Description("Max edges to return (default 30)")] int limit = 30
    )
    {
        var filter = Builders<RelationshipEdge>.Filter.Eq(e => e.FromId, entityId);

        if (!string.IsNullOrWhiteSpace(labelFilter))
        {
            var labels = labelFilter.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            );
            filter &= Builders<RelationshipEdge>.Filter.In(e => e.Label, labels);
        }

        if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont))
            filter &= Builders<RelationshipEdge>.Filter.Eq(e => e.Continuity, cont);

        var edges = await _edges
            .Find(filter)
            .SortByDescending(e => e.Weight)
            .Limit(Math.Min(limit, 50))
            .ToListAsync();

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

        // Group by label for structured LLM consumption
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

        var entityName = edges.First().FromName;

        return JsonSerializer.Serialize(
            new
            {
                entityId,
                entityName,
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
    public async Task<string> GetGraphLabelsForEntity(
        [Description("The PageId of the entity (from search_graph_entities)")] int entityId,
        [Description("Optional continuity filter: Canon, Legends, or omit for all")]
            string? continuity = null
    )
    {
        var matchFilter = new BsonDocument("fromId", entityId);
        if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont))
            matchFilter["continuity"] = cont.ToString();

        var pipeline = new[]
        {
            new BsonDocument("$match", matchFilter),
            new BsonDocument(
                "$group",
                new BsonDocument
                {
                    { "_id", "$label" },
                    { "count", new BsonDocument("$sum", 1) },
                    { "avgWeight", new BsonDocument("$avg", "$weight") },
                }
            ),
            new BsonDocument("$sort", new BsonDocument("count", -1)),
        };

        var edgesCollection = _edges.Database.GetCollection<BsonDocument>(Collections.KgEdges);
        var results = await edgesCollection.Aggregate<BsonDocument>(pipeline).ToListAsync();

        return JsonSerializer.Serialize(
            results.Select(d => new
            {
                label = d["_id"].AsString,
                count = d["count"].AsInt32,
                avgWeight = Math.Round(d["avgWeight"].AsDouble, 2),
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
        [Description("The PageId of the starting entity (from search_graph_entities)")]
            int entityId,
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
        maxDepth = Math.Clamp(maxDepth, 1, 3);

        var edgesCollection = _edges.Database.GetCollection<BsonDocument>(Collections.KgEdges);

        var matchFilter = new BsonDocument("fromId", entityId);
        var restrictMatch = new BsonDocument();

        if (!string.IsNullOrWhiteSpace(labels))
        {
            var labelList = labels.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            );
            var labelBson = new BsonDocument("$in", new BsonArray(labelList));
            matchFilter["label"] = labelBson;
            restrictMatch["label"] = labelBson;
        }

        if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont))
        {
            matchFilter["continuity"] = cont.ToString();
            restrictMatch["continuity"] = cont.ToString();
        }

        // maxDepth represents total hops from root.
        // The $match stage already provides 1 hop (root → X).
        // $graphLookup maxDepth N adds N+1 more hops, so we need maxDepth - 2.
        // When maxDepth <= 1, skip $graphLookup entirely (direct edges only).
        BsonDocument[] pipeline = maxDepth <= 1
            ? [new BsonDocument("$match", matchFilter)]
            :
            [
                new BsonDocument("$match", matchFilter),
                new BsonDocument(
                    "$graphLookup",
                    new BsonDocument
                    {
                        { "from", Collections.KgEdges },
                        { "startWith", "$toId" },
                        { "connectFromField", "toId" },
                        { "connectToField", "fromId" },
                        { "as", "network" },
                        { "maxDepth", maxDepth - 2 },
                        { "depthField", "depth" },
                        { "restrictSearchWithMatch", restrictMatch },
                    }
                ),
            ];

        var results = await edgesCollection.Aggregate<BsonDocument>(pipeline).ToListAsync();

        // Collect unique nodes and edges
        var nodes = new Dictionary<int, (string name, string type, int minDepth)>();
        var edgeList =
            new List<(
                int from,
                string fromName,
                int to,
                string toName,
                string label,
                double weight,
                string evidence,
                int depth
            )>();
        var edgeDedup = new HashSet<(int, int, string)>();

        foreach (var doc in results)
        {
            var toId = doc["toId"].AsInt32;
            var toName = doc["toName"].AsString;
            var toType = doc["toType"].AsString;
            var label = doc["label"].AsString;
            var weight = doc.Contains("weight") ? doc["weight"].ToDouble() : 0.5;
            var evidence = doc.Contains("evidence") ? doc["evidence"].AsString : "";

            if (!nodes.ContainsKey(toId))
                nodes[toId] = (toName, toType, 0);

            if (edgeDedup.Add((entityId, toId, label)))
                edgeList.Add((entityId, "", toId, toName, label, weight, evidence, 0));

            // Network edges from $graphLookup
            if (doc.Contains("network"))
            {
                foreach (var netDoc in doc["network"].AsBsonArray.OfType<BsonDocument>())
                {
                    var nFromId = netDoc["fromId"].AsInt32;
                    var nToId = netDoc["toId"].AsInt32;
                    var nFromName = netDoc["fromName"].AsString;
                    var nToName = netDoc["toName"].AsString;
                    var nFromType = netDoc["fromType"].AsString;
                    var nToType = netDoc["toType"].AsString;
                    var nLabel = netDoc["label"].AsString;
                    var nWeight = netDoc.Contains("weight") ? netDoc["weight"].ToDouble() : 0.5;
                    var nEvidence = netDoc.Contains("evidence") ? netDoc["evidence"].AsString : "";
                    var depth = netDoc.Contains("depth") ? (int)netDoc["depth"].ToInt64() + 1 : 1;

                    if (!nodes.ContainsKey(nFromId))
                        nodes[nFromId] = (nFromName, nFromType, depth);
                    if (!nodes.ContainsKey(nToId))
                        nodes[nToId] = (nToName, nToType, depth);

                    if (edgeDedup.Add((nFromId, nToId, nLabel)))
                        edgeList.Add(
                            (nFromId, nFromName, nToId, nToName, nLabel, nWeight, nEvidence, depth)
                        );
                }
            }
        }

        // Cap output to prevent token overflow
        const int maxNodes = 50;
        const int maxEdges = 80;
        var truncated = nodes.Count > maxNodes || edgeList.Count > maxEdges;

        var sortedEdges = edgeList
            .OrderBy(e => e.depth)
            .ThenByDescending(e => e.weight)
            .Take(maxEdges)
            .ToList();

        var includedNodeIds = new HashSet<int> { entityId };
        foreach (var e in sortedEdges)
        {
            includedNodeIds.Add(e.from);
            includedNodeIds.Add(e.to);
        }

        return JsonSerializer.Serialize(
            new
            {
                root = new { id = entityId },
                nodes = nodes
                    .Where(kv => includedNodeIds.Contains(kv.Key))
                    .Take(maxNodes)
                    .Select(kv => new
                    {
                        pageId = kv.Key,
                        name = kv.Value.name,
                        type = kv.Value.type,
                        hopsFromRoot = kv.Value.minDepth,
                    }),
                edges = sortedEdges.Select(e => new
                {
                    from = e.fromName,
                    to = e.toName,
                    label = e.label,
                    weight = Math.Round(e.weight, 2),
                    evidence = Truncate(e.evidence, 150),
                    depth = e.depth,
                }),
                summary = $"Found {nodes.Count} connected entities and {edgeList.Count} relationships across {maxDepth} hops",
                truncated,
            }
        );
    }

    [Description(
        "Find how two entities are connected in the knowledge graph. "
            + "Returns the shortest path between them with relationship labels and evidence. "
            + "Use for questions like 'How is Palpatine connected to Luke Skywalker?' or "
            + "'What is the relationship between the Jedi Order and the Republic?'. "
            + "Call search_graph_entities first to get PageIds for both entities."
    )]
    public async Task<string> FindConnections(
        [Description("PageId of the first entity")] int entityId1,
        [Description("PageId of the second entity")] int entityId2,
        [Description("Maximum path length to search (default 3, max 4)")] int maxHops = 3,
        [Description("Optional continuity filter: Canon, Legends, or omit for all")]
            string? continuity = null
    )
    {
        maxHops = Math.Clamp(maxHops, 1, 4);

        FilterDefinition<RelationshipEdge>? contFilter = null;
        if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont))
            contFilter = Builders<RelationshipEdge>.Filter.Eq(e => e.Continuity, cont);

        // Bidirectional BFS
        var frontier1 = new HashSet<int> { entityId1 };
        var frontier2 = new HashSet<int> { entityId2 };
        // Maps entityId -> (parent entityId, edge label, edge evidence, direction)
        var visited1 =
            new Dictionary<
                int,
                (int parent, string label, string evidence, string fromName, string toName)
            >();
        var visited2 =
            new Dictionary<
                int,
                (int parent, string label, string evidence, string fromName, string toName)
            >();
        visited1[entityId1] = (-1, "", "", "", "");
        visited2[entityId2] = (-1, "", "", "", "");

        int? meetingPoint = null;
        int hopsUsed = 0;

        // Check immediate: are they the same entity?
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

        for (int hop = 0; hop < maxHops && meetingPoint is null; hop++)
        {
            hopsUsed = hop + 1;

            // Expand smaller frontier
            if (frontier1.Count <= frontier2.Count)
            {
                frontier1 = await ExpandFrontier(frontier1, visited1, contFilter);
                meetingPoint = frontier1.FirstOrDefault(id => visited2.ContainsKey(id));
                if (meetingPoint == 0 && !visited2.ContainsKey(0))
                    meetingPoint = null;
            }
            else
            {
                frontier2 = await ExpandFrontier(frontier2, visited2, contFilter);
                meetingPoint = frontier2.FirstOrDefault(id => visited1.ContainsKey(id));
                if (meetingPoint == 0 && !visited1.ContainsKey(0))
                    meetingPoint = null;
            }

            if (frontier1.Count == 0 && frontier2.Count == 0)
                break;
        }

        if (meetingPoint is null)
        {
            return JsonSerializer.Serialize(
                new
                {
                    connected = false,
                    searchedHops = hopsUsed,
                    note = $"No connection found within {maxHops} hops. The entities may be unrelated or the graph may not have been fully processed.",
                }
            );
        }

        // Reconstruct path
        var path = new List<object>();

        // Path from entity1 to meeting point
        var pathFromE1 =
            new List<(
                int from,
                int to,
                string label,
                string evidence,
                string fromName,
                string toName
            )>();
        var current = meetingPoint.Value;
        while (current != entityId1 && visited1.ContainsKey(current))
        {
            var (parent, label, evidence, fromName, toName) = visited1[current];
            if (parent == -1)
                break;
            pathFromE1.Add((parent, current, label, evidence, fromName, toName));
            current = parent;
        }
        pathFromE1.Reverse();

        // Path from meeting point to entity2
        current = meetingPoint.Value;
        var pathFromE2 =
            new List<(
                int from,
                int to,
                string label,
                string evidence,
                string fromName,
                string toName
            )>();
        while (current != entityId2 && visited2.ContainsKey(current))
        {
            var (parent, label, evidence, fromName, toName) = visited2[current];
            if (parent == -1)
                break;
            pathFromE2.Add((current, parent, label, evidence, fromName, toName));
            current = parent;
        }

        foreach (var step in pathFromE1)
            path.Add(
                new
                {
                    from = step.fromName,
                    to = step.toName,
                    label = step.label,
                    evidence = Truncate(step.evidence, 200),
                }
            );
        foreach (var step in pathFromE2)
            path.Add(
                new
                {
                    from = step.fromName,
                    to = step.toName,
                    label = step.label,
                    evidence = Truncate(step.evidence, 200),
                }
            );

        return JsonSerializer.Serialize(
            new
            {
                connected = true,
                pathLength = path.Count,
                path,
            }
        );
    }

    async Task<HashSet<int>> ExpandFrontier(
        HashSet<int> frontier,
        Dictionary<
            int,
            (int parent, string label, string evidence, string fromName, string toName)
        > visited,
        FilterDefinition<RelationshipEdge>? contFilter
    )
    {
        var filter = Builders<RelationshipEdge>.Filter.In(e => e.FromId, frontier);
        if (contFilter is not null)
            filter &= contFilter;

        var edges = await _edges
            .Find(filter)
            .Limit(500) // Safety cap per frontier expansion
            .ToListAsync();

        var newFrontier = new HashSet<int>();
        foreach (var edge in edges)
        {
            if (!visited.ContainsKey(edge.ToId))
            {
                visited[edge.ToId] = (
                    edge.FromId,
                    edge.Label,
                    edge.Evidence,
                    edge.FromName,
                    edge.ToName
                );
                newFrontier.Add(edge.ToId);
            }
        }

        return newFrontier;
    }

    [Description(
        "Search article content using semantic vector search over chunked wiki pages. "
            + "Returns the most relevant article passages based on meaning, not just keyword matching. "
            + "Use this for lore questions, historical context, event details, or any question where "
            + "the answer is in article body text rather than structured infobox data. "
            + "Combine with graph tools for comprehensive GraphRAG answers."
    )]
    public async Task<string> SearchChunks(
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

        // Generate embedding for the query
        var embeddings = await _embedder.GenerateAsync([query]);
        var queryVector = embeddings[0].Vector.ToArray();

        // Build $vectorSearch pipeline
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

        // Add pre-filter for type and/or continuity
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
                { "text", 1 },
                { "type", 1 },
                { "continuity", 1 },
                { "score", new BsonDocument("$meta", "vectorSearchScore") },
            }
        );

        var pipeline = new[] { vectorSearchStage, projectStage };

        var results = await _chunksRaw.Aggregate<BsonDocument>(pipeline).ToListAsync();

        if (results.Count == 0)
            return JsonSerializer.Serialize(
                new
                {
                    query,
                    results = Array.Empty<object>(),
                    note = "No matching article chunks found. The chunking job may not have run yet, or try a different query.",
                }
            );

        return JsonSerializer.Serialize(
            new
            {
                query,
                results = results.Select(d => new
                {
                    pageId = d["pageId"].AsInt32,
                    title = d["title"].AsString,
                    heading = d.Contains("heading") ? d["heading"].AsString : "",
                    type = d.Contains("type") ? d["type"].AsString : "",
                    continuity = d.Contains("continuity") ? d["continuity"].AsString : "",
                    score = d.Contains("score") ? Math.Round(d["score"].AsDouble, 4) : 0,
                    text = Truncate(d["text"].AsString, 1500),
                }),
                totalResults = results.Count,
            }
        );
    }

    static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? ""
        : s.Length > max ? s[..(max - 1)] + "\u2026"
        : s;

    [Description(
        "Get a complete snapshot of the galaxy at a specific year: territory control (which factions controlled which regions), "
        + "events that occurred (battles, wars, treaties, etc. with locations), and era context. Pre-computed data — instant response. "
        + "Use for questions like 'What was happening in 19 BBY?', 'Who controlled the Outer Rim in 4 ABY?', "
        + "'What battles occurred in 3996 BBY?'. Years use sort-key: negative = BBY, positive = ABY."
    )]
    public async Task<string> GetGalaxyYear(
        [Description("Year in sort-key format (-19 = 19 BBY, 4 = 4 ABY)")] int year
    )
    {
        var doc = await _galaxyYears.Find(d => d.Year == year).FirstOrDefaultAsync();
        if (doc is null)
        {
            // Find nearest available year
            var nearest = await _galaxyYearsRaw
                .Find(Builders<BsonDocument>.Filter.Ne("_id", "overview"))
                .SortBy(d => d["_id"])
                .ToListAsync();
            var available = nearest.Select(d => d["_id"].AsInt32).OrderBy(y => Math.Abs(y - year)).Take(5);
            return JsonSerializer.Serialize(new { error = $"No data for year {year}.", nearestYears = available });
        }

        return JsonSerializer.Serialize(new
        {
            year = doc.Year,
            yearDisplay = doc.YearDisplay,
            era = doc.Era,
            eraDescription = doc.EraDescription,
            territoryControl = doc.Regions.Select(r => new
            {
                region = r.Region,
                factions = r.Factions.Select(f => new { f.Faction, control = $"{f.Control * 100:0}%", f.Contested })
            }),
            eventsOnMap = doc.EventCells.SelectMany(c => c.Events).Select(e => new
            {
                e.Title, e.Lens, e.Place, e.Outcome, e.WikiUrl,
                continuity = e.Continuity.ToString(),
            }).Take(30),
            unresolvedEvents = (doc.UnresolvedEvents ?? []).Select(e => new
            {
                e.Title, e.Lens, e.WikiUrl,
                continuity = e.Continuity.ToString(),
            }).Take(20),
            totalEvents = doc.EventCells.Sum(c => c.Count) + (doc.UnresolvedEvents?.Count ?? 0),
        });
    }

    [Description(
        "Get the temporal lifecycle of an entity — when it was founded/born, when it ended/died, "
        + "and the original date text from Wookieepedia. Use for questions like 'When was the Galactic Empire founded?', "
        + "'How long did the Clone Wars last?', 'When did Yoda die?'. Call search_graph_entities first to get the PageId."
    )]
    public async Task<string> GetEntityTimeline(
        [Description("The PageId of the entity")] int entityId
    )
    {
        var node = await _nodes.Find(n => n.PageId == entityId).FirstOrDefaultAsync();
        if (node is null)
            return JsonSerializer.Serialize(new { error = "Entity not found." });

        // Get temporal edges (relationships with date context)
        var edges = await _edges
            .Find(Builders<RelationshipEdge>.Filter.And(
                Builders<RelationshipEdge>.Filter.Eq(e => e.FromId, entityId),
                Builders<RelationshipEdge>.Filter.Ne(e => e.FromYear, null)))
            .Limit(30)
            .ToListAsync();

        return JsonSerializer.Serialize(new
        {
            pageId = node.PageId,
            name = node.Name,
            type = node.Type,
            continuity = node.Continuity.ToString(),
            universe = node.Universe.ToString(),
            startYear = node.StartYear,
            endYear = node.EndYear,
            startDateText = node.StartDateText,
            endDateText = node.EndDateText,
            duration = node.StartYear.HasValue && node.EndYear.HasValue
                ? $"{Math.Abs(node.EndYear.Value - node.StartYear.Value)} years"
                : null,
            wikiUrl = node.WikiUrl,
            temporalRelationships = edges.Select(e => new
            {
                label = e.Label,
                target = e.ToName,
                fromYear = e.FromYear,
                toYear = e.ToYear,
                evidence = e.Evidence,
            }),
        });
    }

    [Description(
        "List all entity types available in the knowledge graph with counts. Use this to discover valid type values "
        + "for query_entities_by_year and search_graph_entities. Returns types sorted by count descending."
    )]
    public async Task<string> ListKgTypes(
        [Description("Only include types that have temporal data (startYear). Default true.")] bool temporalOnly = true
    )
    {
        var matchStage = temporalOnly
            ? new BsonDocument("$match", new BsonDocument("startYear", new BsonDocument("$ne", BsonNull.Value)))
            : new BsonDocument("$match", new BsonDocument());

        var pipeline = new[]
        {
            matchStage,
            new BsonDocument("$group", new BsonDocument { { "_id", "$type" }, { "count", new BsonDocument("$sum", 1) } }),
            new BsonDocument("$sort", new BsonDocument("count", -1)),
        };

        var results = await _nodes.Database
            .GetCollection<BsonDocument>(Collections.KgNodes)
            .AggregateAsync<BsonDocument>(pipeline);
        var docs = await results.ToListAsync();

        return JsonSerializer.Serialize(docs.Select(d => new
        {
            type = d["_id"].AsString,
            count = d["count"].AsInt32,
        }));
    }

    [Description(
        "List all relationship labels in the knowledge graph with usage counts. Use this to discover what kinds of "
        + "relationships exist (e.g., 'took_place_at', 'affiliated_with', 'homeworld'). Helps choose label filters "
        + "for get_entity_relationships and traverse_graph."
    )]
    public async Task<string> ListKgRelationshipLabels(
        [Description("Max results (default 50)")] int limit = 50
    )
    {
        var pipeline = new[]
        {
            new BsonDocument("$group", new BsonDocument { { "_id", "$label" }, { "count", new BsonDocument("$sum", 1) } }),
            new BsonDocument("$sort", new BsonDocument("count", -1)),
            new BsonDocument("$limit", limit),
        };

        var results = await _edges.Database
            .GetCollection<BsonDocument>(Collections.KgEdges)
            .AggregateAsync<BsonDocument>(pipeline);
        var docs = await results.ToListAsync();

        return JsonSerializer.Serialize(docs.Select(d => new
        {
            label = d["_id"].AsString,
            count = d["count"].AsInt32,
        }));
    }

    public IReadOnlyList<AITool> AsAIFunctions() =>
        [
            AIFunctionFactory.Create(SearchGraphEntities, "search_graph_entities"),
            AIFunctionFactory.Create(QueryEntitiesByYear, "query_entities_by_year"),
            AIFunctionFactory.Create(GetEntityProperties, "get_entity_properties"),
            AIFunctionFactory.Create(GetEntityTimeline, "get_entity_timeline"),
            AIFunctionFactory.Create(GetEntityRelationships, "get_entity_relationships"),
            AIFunctionFactory.Create(GetGraphLabelsForEntity, "get_graph_labels"),
            AIFunctionFactory.Create(TraverseGraph, "traverse_graph"),
            AIFunctionFactory.Create(FindConnections, "find_connections"),
            AIFunctionFactory.Create(GetGalaxyYear, "get_galaxy_year"),
            AIFunctionFactory.Create(ListKgTypes, "list_kg_types"),
            AIFunctionFactory.Create(ListKgRelationshipLabels, "list_kg_relationship_labels"),
            AIFunctionFactory.Create(SearchChunks, "search_chunks"),
        ];
}
