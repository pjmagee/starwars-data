using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries;

namespace StarWarsData.Services;

/// <summary>
/// Design-032 — "Events at this location". Lists every event the KG records
/// as having happened at a focused spatial entity (System / CelestialBody /
/// Sector / Region / TradeRoute). Date-agnostic; Timeline mode keeps its
/// year-scoped role unchanged.
///
/// Phase 1 reads <c>kg.edges</c> directly, the same pattern
/// <see cref="KnowledgeGraph.KnowledgeGraphQueryService"/> uses — incoming
/// spatial-event edges (<see cref="SpatialEventLabels"/>) whose source node
/// type is in the event family. Edges already denormalize source name/type/
/// continuity/realm and the Design-021 temporal bound, so the only join is a
/// batch lookup against <c>kg.nodes</c> for the source's wiki URL.
/// </summary>
public class EventsAtLocationService(ILogger<EventsAtLocationService> logger, IOptions<SettingsOptions> settingsOptions, IMongoClient mongoClient)
{
    readonly IMongoCollection<GraphNode> _nodes = mongoClient.GetDatabase(settingsOptions.Value.DatabaseName).GetCollection<GraphNode>(Collections.KgNodes);

    readonly IMongoCollection<RelationshipEdge> _edges = mongoClient.GetDatabase(settingsOptions.Value.DatabaseName).GetCollection<RelationshipEdge>(Collections.KgEdges);

    /// <summary>
    /// Returns the events that took place at <paramref name="pageId"/>. Returns
    /// null when the pageId doesn't resolve to a KG node at all (404 upstream);
    /// an empty <see cref="EventsAtLocationResult.Events"/> when it resolves but
    /// has no spatial-event edges.
    /// </summary>
    public async Task<EventsAtLocationResult?> GetEventsAtAsync(int pageId, Continuity? continuity = null, Realm? realm = null, CancellationToken ct = default)
    {
        var location = await _nodes
            .Find(n => n.PageId == pageId)
            .Project(n => new
            {
                n.PageId,
                n.Name,
                n.Type,
            })
            .FirstOrDefaultAsync(ct);
        if (location is null)
            return null;

        // Design-032 Phase 2 roll-up. A user standing on a System/Sector/Region
        // expects the events of the worlds it contains, not just the (usually
        // empty) set of edges pointing at the container node itself. Containment
        // is the inverse of the in_system / in_sector / in_region edges every
        // contained entity carries. One bulk hop, capped so a huge region can't
        // blow out the query.
        const int MaxDescendants = 4000;
        var (scope, containmentLabels) = location.Type switch
        {
            KgNodeTypes.System => ("system", new[] { "in_system", "system" }),
            KgNodeTypes.Sector => ("sector", new[] { "in_sector" }),
            KgNodeTypes.Region => ("region", new[] { "in_region" }),
            _ => ("location", null),
        };

        var targetIds = new HashSet<int> { pageId };
        var rolledUp = 0;
        if (containmentLabels is not null)
        {
            var cf = Builders<RelationshipEdge>.Filter;
            var descendantIds = await _edges.Find(cf.And(cf.Eq(e => e.ToId, pageId), cf.In(e => e.Label, containmentLabels))).Project(e => e.FromId).Limit(MaxDescendants).ToListAsync(ct);
            foreach (var id in descendantIds)
                if (targetIds.Add(id))
                    rolledUp++;
        }

        var f = Builders<RelationshipEdge>.Filter;
        var filter = f.And(
            targetIds.Count == 1 ? f.Eq(e => e.ToId, pageId) : f.In(e => e.ToId, targetIds),
            f.In(e => e.Label, SpatialEventLabels.Labels),
            f.In(e => e.FromType, SpatialEventLabels.EventFamily)
        );
        if (continuity is { } c)
            filter &= f.Eq(e => e.Continuity, c);
        if (realm is { } r)
            // Never hide Unknown — same safety-net rule the Realm enum documents.
            filter &= f.In(e => e.FromRealm, new[] { r, Realm.Unknown });

        var edges = await _edges.Find(filter).ToListAsync(ct);

        // Risk note (Design-032): an event can reach the same place via several
        // infobox sources (happened_at AND took_place_at). Collapse to one row
        // per source event, keeping the most-specific label and the tightest
        // known year.
        var byEvent = new Dictionary<int, RelationshipEdge>();
        foreach (var e in edges)
        {
            if (!byEvent.TryGetValue(e.FromId, out var existing) || LabelRank(e.Label) < LabelRank(existing.Label))
                byEvent[e.FromId] = e;
            else if (existing.FromYear is null && e.FromYear is not null)
                byEvent[e.FromId] = e;
        }

        // One batch lookup for wiki URLs — edges carry everything else.
        var sourceIds = byEvent.Keys.ToList();
        var wikiUrls =
            sourceIds.Count == 0
                ? new Dictionary<int, string?>()
                : (await _nodes.Find(Builders<GraphNode>.Filter.In(n => n.PageId, sourceIds)).Project(n => new { n.PageId, n.WikiUrl }).ToListAsync(ct)).ToDictionary(x => x.PageId, x => x.WikiUrl);

        var events = byEvent
            .Values.Select(e => new LocationEvent
            {
                PageId = e.FromId,
                Name = e.FromName,
                Type = e.FromType,
                Category = SpatialEventLabels.CategoryFor(e.FromType),
                Continuity = e.Continuity.ToString(),
                Year = e.FromYear,
                YearDisplay = FormatYear(e.FromYear),
                WikiUrl = wikiUrls.GetValueOrDefault(e.FromId),
                EdgeLabel = e.Label,
            })
            // BBY → ABY, nullable years last, then by name for stable ordering.
            .OrderBy(e => e.Year is null)
            .ThenBy(e => e.Year ?? int.MaxValue)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        logger.LogInformation(
            "EventsAtLocation {LocationId} ({Name}, scope={Scope}, rolledUp={RolledUp}): {Edges} edges → {Events} events (continuity={Continuity}, realm={Realm})",
            pageId,
            location.Name,
            scope,
            rolledUp,
            edges.Count,
            events.Count,
            continuity,
            realm
        );

        return new EventsAtLocationResult
        {
            LocationId = location.PageId,
            LocationName = location.Name,
            TotalEvents = events.Count,
            Events = events,
            Scope = scope,
            RolledUpLocations = rolledUp,
        };
    }

    // Lower rank = more specific / preferred when an event has several spatial
    // edges to the same place. took_place_at is what the corpus actually uses;
    // the generic located_at is the weakest signal.
    static int LabelRank(string label) =>
        label.ToLowerInvariant() switch
        {
            "took_place_at" => 0,
            "fought_at" => 1,
            "hosted_battle" => 1,
            "site_of" => 2,
            "conducted_at" => 2,
            "occurred_at" => 3,
            "happened_at" => 3,
            "happened_in" => 4,
            "located_at" => 5,
            _ => 9,
        };

    // Matches the galaxy-map ETL convention; year 0 is the BBY/ABY pivot and
    // has no sign, so it's rendered "0 BBY/ABY" per the Design-032 example.
    static string FormatYear(int? year) =>
        year switch
        {
            null => "Unknown",
            0 => "0 BBY/ABY",
            < 0 => $"{-year.Value} BBY",
            > 0 => $"{year.Value} ABY",
        };
}
