using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries;

namespace StarWarsData.ApiService.Controllers;

/// <summary>
/// Public read-only endpoints for the knowledge graph (kg.nodes + kg.edges).
/// Powers the Graph Explorer frontend page.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class RelationshipGraphController : ControllerBase
{
    readonly IMongoCollection<GraphNode> _nodes;
    readonly IMongoCollection<RelationshipEdge> _edges;

    public RelationshipGraphController(IMongoClient mongoClient, IOptions<SettingsOptions> settings)
    {
        var db = mongoClient.GetDatabase(settings.Value.DatabaseName);
        _nodes = db.GetCollection<GraphNode>(Collections.KgNodes);
        _edges = db.GetCollection<RelationshipEdge>(Collections.KgEdges);
    }

    /// <summary>Distinct entity types that exist in the knowledge graph.</summary>
    [HttpGet("entity-types")]
    public async Task<List<string>> GetEntityTypes(CancellationToken ct)
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

    /// <summary>All distinct edge labels in the knowledge graph.</summary>
    [HttpGet("edge-labels")]
    public async Task<List<string>> GetEdgeLabels(CancellationToken ct)
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

    /// <summary>Browse entities by type with pagination.</summary>
    [HttpGet("browse")]
    public async Task<BrowseEntitiesResult> Browse(
        [FromQuery] string? type = null,
        [FromQuery] string? q = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? continuity = null,
        CancellationToken ct = default
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

    /// <summary>Search entities by name.</summary>
    [HttpGet("search")]
    public async Task<List<EntitySearchDto>> Search(
        [FromQuery] string q,
        [FromQuery] string? type = null,
        [FromQuery] string? continuity = null,
        CancellationToken ct = default
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

    /// <summary>Get edge labels for a specific entity (for the label picker).</summary>
    [HttpGet("labels/{pageId:int}")]
    public async Task<List<string>> GetLabels(int pageId, CancellationToken ct)
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

    /// <summary>Browse knowledge graph nodes with temporal data and properties.</summary>
    [HttpGet("temporal-nodes")]
    public async Task<BrowseTemporalNodesResult> BrowseTemporalNodes(
        [FromQuery] string? type = null,
        [FromQuery] string? q = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? continuity = null,
        [FromQuery] bool temporalOnly = false,
        [FromQuery] int? yearFrom = null,
        [FromQuery] int? yearTo = null,
        [FromQuery] string? semantic = null,
        [FromQuery] string? label = null,
        CancellationToken ct = default
    )
    {
        if (pageSize > 100)
            pageSize = 100;

        // If filtering by edge label, first find which node IDs have edges with that label
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

    /// <summary>
    /// Traverse the graph from an entity, returning nodes and edges for D3 rendering.
    /// Uses the RelationshipGraphResult DTO expected by the frontend.
    /// </summary>
    [HttpGet("query/{pageId:int}")]
    public async Task<RelationshipGraphResult> QueryGraph(
        int pageId,
        [FromQuery] string? labels = null,
        [FromQuery] int maxDepth = 2,
        [FromQuery] string? continuity = null,
        CancellationToken ct = default
    )
    {
        maxDepth = Math.Clamp(maxDepth, 1, 4);
        var edgesRaw = _edges.Database.GetCollection<BsonDocument>(Collections.KgEdges);

        // BFS traversal from the root entity
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

        // Build result
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
                // Edge target not in nodes collection (stub)
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
