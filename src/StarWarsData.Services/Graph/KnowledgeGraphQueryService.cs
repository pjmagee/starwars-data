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
}
