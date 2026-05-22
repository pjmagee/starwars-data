using Microsoft.Extensions.Options;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries;
using StarWarsData.Services.AI.RequestContext;

namespace StarWarsData.Services.AI.Citations;

/// <inheritdoc />
public sealed class CitationResolver : ICitationResolver
{
    static readonly HashSet<string> DirectSpatialTypes = new(StringComparer.Ordinal) { KgNodeTypes.System, KgNodeTypes.CelestialBody, KgNodeTypes.Sector, KgNodeTypes.Region, KgNodeTypes.TradeRoute };

    // Design-030 Phase 2: a non-spatial node earns a Galaxy Map link when it
    // has one of these outgoing edges to a Direct spatial target — a battle's
    // battlefield, a character's homeworld, a book's setting. SpatialEventLabels
    // (Design-032) is folded in so an event's "took_place_at" also counts and
    // the resolver can emit the ?event= round-trip.
    static readonly HashSet<string> IndirectSpatialLabels = new(
        new[] { "located_at", "homeworld", "birthplace", "born_on", "setting", "fought_at" }.Concat(SpatialEventLabels.Labels),
        StringComparer.OrdinalIgnoreCase
    );

    readonly IMongoCollection<GraphNode> _nodes;
    readonly IMongoCollection<RelationshipEdge> _edges;
    readonly ICurrentRequestContext _requestContext;

    public CitationResolver(IOptions<SettingsOptions> settingsOptions, IMongoClient mongoClient, ICurrentRequestContext requestContext)
    {
        var db = mongoClient.GetDatabase(settingsOptions.Value.DatabaseName);
        _nodes = db.GetCollection<GraphNode>(Collections.KgNodes);
        _edges = db.GetCollection<RelationshipEdge>(Collections.KgEdges);
        _requestContext = requestContext;
    }

    public async Task<IReadOnlyList<CitationReference>> ResolveAsync(IReadOnlyList<int> pageIds, CancellationToken ct = default)
    {
        if (pageIds.Count == 0)
            return [];

        var distinctIds = pageIds.Distinct().ToArray();

        // Single bulk read of nodes. We project the small slice we need so we
        // don't pay for whole-document deserialization on the citation hot path.
        var nodes = await _nodes
            .Find(Builders<GraphNode>.Filter.In(n => n.PageId, distinctIds))
            .Project(n => new
            {
                n.PageId,
                n.Name,
                n.Type,
                n.Continuity,
                n.ImageUrl,
                n.WikiUrl,
            })
            .ToListAsync(ct);

        var byId = nodes.ToDictionary(n => n.PageId);

        // The set of ids that don't get a Direct galaxy-map link — candidates
        // for the Phase 2 indirect hop. Skip the lookup entirely when there are
        // none (every cited entity was itself spatial).
        var indirectCandidates = distinctIds.Where(id => byId.TryGetValue(id, out var n) && !DirectSpatialTypes.Contains(n.Type)).ToArray();

        var indirectTarget = indirectCandidates.Length == 0 ? new Dictionary<int, int>() : await ResolveIndirectSpatialTargetsAsync(indirectCandidates, ct);

        // Preserve caller order. Missing ids return a minimal "Unknown" reference
        // with only the wiki link populated when we know nothing else — keeps
        // the UI from breaking on a stale id while still rendering something.
        var results = new List<CitationReference>(distinctIds.Length);
        foreach (var id in distinctIds)
        {
            if (!byId.TryGetValue(id, out var n))
            {
                results.Add(
                    new CitationReference(
                        PageId: id,
                        Name: $"Entity #{id}",
                        Kind: "Unknown",
                        Continuity: null,
                        ImageUrl: null,
                        Links: new CitationLinks(Wiki: null, KnowledgeGraph: null, GalaxyMap: null)
                    )
                );
                continue;
            }

            string? galaxyMap = null;
            if (DirectSpatialTypes.Contains(n.Type))
            {
                galaxyMap = $"/galaxy-map/{n.PageId}";
            }
            else if (indirectTarget.TryGetValue(n.PageId, out var locId))
            {
                // Design-032 round-trip: when the cited node is itself an event,
                // carry its id as ?event= so the location lands with the event
                // highlighted and the battle stays in view.
                galaxyMap = SpatialEventLabels.EventFamily.Contains(n.Type) ? $"/galaxy-map/{locId}?event={n.PageId}" : $"/galaxy-map/{locId}";
            }

            results.Add(
                new CitationReference(
                    PageId: n.PageId,
                    Name: n.Name,
                    Kind: n.Type,
                    Continuity: n.Continuity.ToString(),
                    ImageUrl: n.ImageUrl,
                    // KnowledgeGraph is the canonical node-detail surface for every
                    // KG entity, so it's always populated when the node was found.
                    // Graph Explorer / Character Timeline / Holocron remain reachable
                    // from inside that page (Design-030 follow-up).
                    Links: new CitationLinks(Wiki: n.WikiUrl, KnowledgeGraph: $"/knowledge-graph/nodes/{n.PageId}", GalaxyMap: galaxyMap)
                )
            );
        }

        return results;
    }

    /// <summary>
    /// One bulk <c>kg.edges</c> query: for each candidate source id, the pageId
    /// of the first spatial node it points at via an <see cref="IndirectSpatialLabels"/>
    /// edge. <c>toType</c> is denormalized on the edge so no join is needed.
    /// Honours the request-scoped continuity (Design-029) so we don't hand the
    /// user a map link to a target their own filter would hide.
    /// </summary>
    async Task<Dictionary<int, int>> ResolveIndirectSpatialTargetsAsync(IReadOnlyList<int> sourceIds, CancellationToken ct)
    {
        var f = Builders<RelationshipEdge>.Filter;
        var filter = f.And(f.In(e => e.FromId, sourceIds), f.In(e => e.ToType, DirectSpatialTypes), f.In(e => e.Label, IndirectSpatialLabels));
        if (_requestContext.Continuity is { } c)
            // edge.Continuity is the denormalized source continuity; Unknown is
            // never hidden (same safety-net rule the enum documents).
            filter &= f.In(e => e.Continuity, new[] { c, Continuity.Unknown });

        var edges = await _edges.Find(filter).SortByDescending(e => e.Weight).Project(e => new { e.FromId, e.ToId }).ToListAsync(ct);

        // Highest-weight edge wins per source (SortByDescending + first-write).
        var map = new Dictionary<int, int>();
        foreach (var e in edges)
            map.TryAdd(e.FromId, e.ToId);
        return map;
    }
}
