using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries;

namespace StarWarsData.Services;

/// <summary>
/// Read-only query service for the knowledge graph (kg.nodes + kg.edges).
/// Powers the Graph Explorer page, Knowledge Graph page, and render_graph tool.
/// </summary>
public class KnowledgeGraphQueryService(
    IMongoClient mongoClient,
    IOptions<SettingsOptions> settings
)
{
    readonly IMongoCollection<GraphNode> _nodes = mongoClient
        .GetDatabase(settings.Value.DatabaseName)
        .GetCollection<GraphNode>(Collections.KgNodes);

    readonly IMongoCollection<RelationshipEdge> _edges = mongoClient
        .GetDatabase(settings.Value.DatabaseName)
        .GetCollection<RelationshipEdge>(Collections.KgEdges);

    public async Task<List<string>> GetEntityTypesAsync(CancellationToken ct)
    {
        var types = await _nodes.DistinctAsync(
            new StringFieldDefinition<GraphNode, string>("type"),
            FilterDefinition<GraphNode>.Empty,
            cancellationToken: ct
        );
        return (await types.ToListAsync(ct))
            .Where(t => !string.IsNullOrEmpty(t))
            .OrderBy(t => t)
            .ToList();
    }

    public async Task<List<string>> GetEdgeLabelsAsync(CancellationToken ct)
    {
        var labels = await _edges.DistinctAsync(
            new StringFieldDefinition<RelationshipEdge, string>("label"),
            FilterDefinition<RelationshipEdge>.Empty,
            cancellationToken: ct
        );
        return (await labels.ToListAsync(ct))
            .Where(l => !string.IsNullOrEmpty(l))
            .OrderBy(l => l)
            .ToList();
    }

    public async Task<BrowseEntitiesResult> BrowseAsync(
        string? type,
        string? q,
        int page,
        int pageSize,
        string? continuity,
        CancellationToken ct
    )
    {
        if (pageSize > 100)
            pageSize = 100;

        var filters = new List<FilterDefinition<GraphNode>>();

        if (!string.IsNullOrWhiteSpace(type))
            filters.Add(Builders<GraphNode>.Filter.Eq(n => n.Type, type));
        if (!string.IsNullOrWhiteSpace(q))
            filters.Add(
                Builders<GraphNode>.Filter.Regex(n => n.Name, new BsonRegularExpression(q, "i"))
            );
        if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont))
            filters.Add(Builders<GraphNode>.Filter.Eq(n => n.Continuity, cont));

        var filter =
            filters.Count > 0
                ? Builders<GraphNode>.Filter.And(filters)
                : FilterDefinition<GraphNode>.Empty;

        var total = await _nodes.CountDocumentsAsync(filter, cancellationToken: ct);
        var items = await _nodes
            .Find(filter)
            .SortBy(n => n.Name)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .Project(n => new EntitySearchDto
            {
                Id = n.PageId,
                Name = n.Name,
                Type = n.Type,
                Continuity = n.Continuity.ToString(),
            })
            .ToListAsync(ct);

        return new BrowseEntitiesResult
        {
            Items = items,
            Total = total,
            Page = page,
            PageSize = pageSize,
        };
    }

    public async Task<List<EntitySearchDto>> SearchAsync(
        string q,
        string? type,
        string? continuity,
        CancellationToken ct
    )
    {
        var filters = new List<FilterDefinition<GraphNode>>
        {
            Builders<GraphNode>.Filter.Regex(n => n.Name, new BsonRegularExpression(q, "i")),
        };

        if (!string.IsNullOrWhiteSpace(type))
            filters.Add(Builders<GraphNode>.Filter.Eq(n => n.Type, type));
        if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont))
            filters.Add(Builders<GraphNode>.Filter.Eq(n => n.Continuity, cont));

        return await _nodes
            .Find(Builders<GraphNode>.Filter.And(filters))
            .SortBy(n => n.Name)
            .Limit(30)
            .Project(n => new EntitySearchDto
            {
                Id = n.PageId,
                Name = n.Name,
                Type = n.Type,
                Continuity = n.Continuity.ToString(),
            })
            .ToListAsync(ct);
    }

    public async Task<List<string>> GetLabelsForEntityAsync(int pageId, CancellationToken ct)
    {
        var pipeline = new[]
        {
            new BsonDocument("$match", new BsonDocument("fromId", pageId)),
            new BsonDocument("$group", new BsonDocument("_id", "$label")),
            new BsonDocument("$sort", new BsonDocument("_id", 1)),
        };

        var results = await _edges
            .Database.GetCollection<BsonDocument>(Collections.KgEdges)
            .Aggregate<BsonDocument>(pipeline)
            .ToListAsync(ct);

        return results.Select(d => d["_id"].AsString).ToList();
    }

    public async Task<BrowseTemporalNodesResult> BrowseTemporalNodesAsync(
        string? type,
        string? q,
        int page,
        int pageSize,
        string? continuity,
        bool temporalOnly,
        int? yearFrom,
        int? yearTo,
        string? semantic,
        string? label,
        CancellationToken ct
    )
    {
        if (pageSize > 100)
            pageSize = 100;

        HashSet<int>? nodeIdsWithLabel = null;
        if (!string.IsNullOrWhiteSpace(label))
        {
            var edgeFilter = Builders<RelationshipEdge>.Filter.Eq(e => e.Label, label);
            var matchingFromIds = await _edges
                .Distinct(
                    new StringFieldDefinition<RelationshipEdge, int>("fromId"),
                    edgeFilter,
                    cancellationToken: ct
                )
                .ToListAsync(ct);
            nodeIdsWithLabel = [.. matchingFromIds];
        }

        var filters = new List<FilterDefinition<GraphNode>>();

        if (nodeIdsWithLabel is not null)
            filters.Add(Builders<GraphNode>.Filter.In(n => n.PageId, nodeIdsWithLabel));
        if (!string.IsNullOrWhiteSpace(type))
            filters.Add(Builders<GraphNode>.Filter.Eq(n => n.Type, type));
        if (!string.IsNullOrWhiteSpace(q))
            filters.Add(
                Builders<GraphNode>.Filter.Regex(n => n.Name, new BsonRegularExpression(q, "i"))
            );
        if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont))
            filters.Add(Builders<GraphNode>.Filter.Eq(n => n.Continuity, cont));
        if (temporalOnly)
            filters.Add(Builders<GraphNode>.Filter.SizeGt(n => n.TemporalFacets, 0));
        if (!string.IsNullOrWhiteSpace(semantic))
            filters.Add(
                Builders<GraphNode>.Filter.Regex(
                    "temporalFacets.semantic",
                    new BsonRegularExpression($"^{Regex.Escape(semantic)}", "i")
                )
            );
        if (yearFrom.HasValue)
            filters.Add(
                Builders<GraphNode>.Filter.Or(
                    Builders<GraphNode>.Filter.Gte(n => n.EndYear, yearFrom.Value),
                    Builders<GraphNode>.Filter.Eq(n => n.EndYear, null)
                )
            );
        if (yearTo.HasValue)
            filters.Add(Builders<GraphNode>.Filter.Lte(n => n.StartYear, yearTo.Value));

        var filter =
            filters.Count > 0
                ? Builders<GraphNode>.Filter.And(filters)
                : FilterDefinition<GraphNode>.Empty;

        var total = await _nodes.CountDocumentsAsync(filter, cancellationToken: ct);
        var items = await _nodes
            .Find(filter)
            .SortBy(n => n.Name)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);

        var dtos = items
            .Select(n => new TemporalNodeDto
            {
                Id = n.PageId,
                Name = n.Name,
                Type = n.Type,
                Continuity = n.Continuity.ToString(),
                ImageUrl = n.ImageUrl,
                WikiUrl = n.WikiUrl,
                StartYear = n.StartYear,
                EndYear = n.EndYear,
                StartDateText = n.StartDateText,
                EndDateText = n.EndDateText,
                Properties = n.Properties,
                TemporalFacets = n.TemporalFacets,
            })
            .ToList();

        return new BrowseTemporalNodesResult
        {
            Items = dtos,
            Total = total,
            Page = page,
            PageSize = pageSize,
        };
    }

    public async Task<RelationshipGraphResult> QueryGraphAsync(
        int pageId,
        string? labels,
        int maxDepth,
        string? continuity,
        CancellationToken ct
    )
    {
        maxDepth = Math.Clamp(maxDepth, 1, 4);
        var edgesRaw = _edges.Database.GetCollection<BsonDocument>(Collections.KgEdges);

        var visited = new HashSet<int> { pageId };
        var allEdges =
            new List<(
                int from,
                string fromName,
                int to,
                string toName,
                string label,
                double weight
            )>();
        var frontier = new HashSet<int> { pageId };

        for (var depth = 0; depth < maxDepth && frontier.Count > 0; depth++)
        {
            var filter = Builders<BsonDocument>.Filter.In("fromId", frontier);
            if (!string.IsNullOrWhiteSpace(labels))
            {
                var labelList = labels.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                );
                filter &= Builders<BsonDocument>.Filter.In("label", labelList);
            }
            if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont))
                filter &= Builders<BsonDocument>.Filter.Eq("continuity", cont.ToString());

            var edges = await edgesRaw.Find(filter).Limit(500).ToListAsync(ct);
            var nextFrontier = new HashSet<int>();

            foreach (var e in edges)
            {
                var toId = e["toId"].AsInt32;
                var fromId = e["fromId"].AsInt32;
                allEdges.Add(
                    (
                        fromId,
                        e["fromName"].AsString,
                        toId,
                        e["toName"].AsString,
                        e["label"].AsString,
                        e.Contains("weight") ? e["weight"].ToDouble() : 0.8
                    )
                );

                if (visited.Add(toId))
                    nextFrontier.Add(toId);
            }

            frontier = nextFrontier;
        }

        var rootNode = await _nodes.Find(n => n.PageId == pageId).FirstOrDefaultAsync(ct);
        var nodeIds = allEdges.SelectMany(e => new[] { e.from, e.to }).Distinct().ToList();
        var nodeDocs = await _nodes
            .Find(Builders<GraphNode>.Filter.In(n => n.PageId, nodeIds))
            .ToListAsync(ct);
        var nodeMap = nodeDocs.ToDictionary(n => n.PageId);

        var resultNodes = new List<RelationshipGraphNode>();
        foreach (var id in nodeIds)
        {
            if (nodeMap.TryGetValue(id, out var node))
            {
                resultNodes.Add(
                    new RelationshipGraphNode
                    {
                        Id = node.PageId,
                        Name = node.Name,
                        Type = node.Type,
                        ImageUrl = node.ImageUrl,
                    }
                );
            }
            else
            {
                var edge = allEdges.First(e => e.to == id);
                resultNodes.Add(
                    new RelationshipGraphNode
                    {
                        Id = id,
                        Name = edge.toName,
                        Type = "",
                    }
                );
            }
        }

        var resultEdges = allEdges
            .DistinctBy(e => (e.from, e.to, e.label))
            .Take(200)
            .Select(e => new RelationshipGraphEdge
            {
                FromId = e.from,
                ToId = e.to,
                Label = e.label,
                Weight = e.weight,
            })
            .ToList();

        return new RelationshipGraphResult
        {
            RootId = pageId,
            RootName = rootNode?.Name ?? $"#{pageId}",
            Nodes = resultNodes,
            Edges = resultEdges,
        };
    }

    // ── Core query methods shared by GraphRAGToolkit and controller ──

    /// <summary>Search nodes by name with optional type/continuity filter.</summary>
    public async Task<List<GraphNode>> SearchNodesAsync(
        string query,
        string? type,
        string? continuity,
        int limit,
        CancellationToken ct = default
    )
    {
        var filters = new List<FilterDefinition<GraphNode>>
        {
            Builders<GraphNode>.Filter.Regex(n => n.Name, new BsonRegularExpression(query, "i")),
        };
        if (!string.IsNullOrWhiteSpace(type))
            filters.Add(Builders<GraphNode>.Filter.Eq(n => n.Type, type));
        if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont))
            filters.Add(Builders<GraphNode>.Filter.Eq(n => n.Continuity, cont));

        return await _nodes
            .Find(Builders<GraphNode>.Filter.And(filters))
            .SortBy(n => n.Name)
            .Limit(Math.Min(limit, 50))
            .ToListAsync(ct);
    }

    /// <summary>Get a single node by PageId.</summary>
    public Task<GraphNode?> GetNodeByIdAsync(int pageId, CancellationToken ct = default) =>
        _nodes.Find(n => n.PageId == pageId).FirstOrDefaultAsync(ct)!;

    /// <summary>Find nodes by temporal range with optional semantic dimension filter.</summary>
    public async Task<List<GraphNode>> FindNodesByYearAsync(
        int year,
        string type,
        int? yearEnd,
        string? continuity,
        string? semantic,
        int limit,
        CancellationToken ct = default
    )
    {
        var rangeStart = Math.Min(year, yearEnd ?? year);
        var rangeEnd = Math.Max(year, yearEnd ?? year);

        var filters = new List<FilterDefinition<GraphNode>>
        {
            Builders<GraphNode>.Filter.Eq(n => n.Type, type),
        };

        if (!string.IsNullOrWhiteSpace(semantic))
        {
            filters.Add(
                Builders<GraphNode>.Filter.ElemMatch(
                    n => n.TemporalFacets,
                    Builders<TemporalFacet>.Filter.And(
                        Builders<TemporalFacet>.Filter.Regex(
                            f => f.Semantic,
                            new BsonRegularExpression($"^{semantic}", "i")
                        ),
                        Builders<TemporalFacet>.Filter.Lte(f => f.Year, rangeEnd),
                        Builders<TemporalFacet>.Filter.Gte(f => f.Year, rangeStart)
                    )
                )
            );
        }
        else
        {
            filters.Add(Builders<GraphNode>.Filter.Lte(n => n.StartYear, rangeEnd));
            filters.Add(
                Builders<GraphNode>.Filter.Or(
                    Builders<GraphNode>.Filter.Eq(n => n.EndYear, (int?)null),
                    Builders<GraphNode>.Filter.Gte(n => n.EndYear, rangeStart)
                )
            );
        }

        if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont))
            filters.Add(Builders<GraphNode>.Filter.Eq(n => n.Continuity, cont));

        return await _nodes
            .Find(Builders<GraphNode>.Filter.And(filters))
            .SortBy(n => n.Name)
            .Limit(Math.Min(limit, 50))
            .ToListAsync(ct);
    }

    /// <summary>Get direct edges from an entity, optionally filtered by labels and continuity.</summary>
    public async Task<List<RelationshipEdge>> GetEdgesFromEntityAsync(
        int entityId,
        string? labelFilter,
        string? continuity,
        int limit,
        CancellationToken ct = default
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

        return await _edges
            .Find(filter)
            .SortByDescending(e => e.Weight)
            .Limit(Math.Min(limit, 50))
            .ToListAsync(ct);
    }

    /// <summary>Get relationship type summary (label + count + avgWeight) for an entity.</summary>
    public async Task<List<(string label, int count, double avgWeight)>> GetRelationshipTypesAsync(
        int entityId,
        string? continuity,
        CancellationToken ct = default
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

        var results = await _edges
            .Database.GetCollection<BsonDocument>(Collections.KgEdges)
            .Aggregate<BsonDocument>(pipeline)
            .ToListAsync(ct);

        return results
            .Select(d =>
                (
                    label: d["_id"].AsString,
                    count: d["count"].AsInt32,
                    avgWeight: d["avgWeight"].AsDouble
                )
            )
            .ToList();
    }

    /// <summary>Bidirectional BFS to find the shortest path between two entities.</summary>
    public async Task<(
        bool connected,
        List<(int from, int to, string label, string evidence, string fromName, string toName)> path
    )> FindConnectionsAsync(
        int entityId1,
        int entityId2,
        int maxHops,
        string? continuity,
        CancellationToken ct = default
    )
    {
        maxHops = Math.Clamp(maxHops, 1, 4);

        FilterDefinition<RelationshipEdge>? contFilter = null;
        if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont))
            contFilter = Builders<RelationshipEdge>.Filter.Eq(e => e.Continuity, cont);

        if (entityId1 == entityId2)
            return (true, []);

        var frontier1 = new HashSet<int> { entityId1 };
        var frontier2 = new HashSet<int> { entityId2 };
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

        for (int hop = 0; hop < maxHops && meetingPoint is null; hop++)
        {
            if (frontier1.Count <= frontier2.Count)
            {
                frontier1 = await ExpandFrontierAsync(frontier1, visited1, contFilter, ct);
                meetingPoint = frontier1.FirstOrDefault(id => visited2.ContainsKey(id));
                if (meetingPoint == 0 && !visited2.ContainsKey(0))
                    meetingPoint = null;
            }
            else
            {
                frontier2 = await ExpandFrontierAsync(frontier2, visited2, contFilter, ct);
                meetingPoint = frontier2.FirstOrDefault(id => visited1.ContainsKey(id));
                if (meetingPoint == 0 && !visited1.ContainsKey(0))
                    meetingPoint = null;
            }

            if (frontier1.Count == 0 && frontier2.Count == 0)
                break;
        }

        if (meetingPoint is null)
            return (false, []);

        var path =
            new List<(
                int from,
                int to,
                string label,
                string evidence,
                string fromName,
                string toName
            )>();

        var current = meetingPoint.Value;
        var pathFromE1 =
            new List<(
                int from,
                int to,
                string label,
                string evidence,
                string fromName,
                string toName
            )>();
        while (current != entityId1 && visited1.ContainsKey(current))
        {
            var (parent, label, evidence, fromName, toName) = visited1[current];
            if (parent == -1)
                break;
            pathFromE1.Add((parent, current, label, evidence, fromName, toName));
            current = parent;
        }
        pathFromE1.Reverse();

        current = meetingPoint.Value;
        while (current != entityId2 && visited2.ContainsKey(current))
        {
            var (parent, label, evidence, fromName, toName) = visited2[current];
            if (parent == -1)
                break;
            path.Add((current, parent, label, evidence, fromName, toName));
            current = parent;
        }

        pathFromE1.AddRange(path);
        return (true, pathFromE1);
    }

    async Task<HashSet<int>> ExpandFrontierAsync(
        HashSet<int> frontier,
        Dictionary<
            int,
            (int parent, string label, string evidence, string fromName, string toName)
        > visited,
        FilterDefinition<RelationshipEdge>? contFilter,
        CancellationToken ct
    )
    {
        var filter = Builders<RelationshipEdge>.Filter.In(e => e.FromId, frontier);
        if (contFilter is not null)
            filter &= contFilter;

        var edges = await _edges.Find(filter).Limit(500).ToListAsync(ct);
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
}
