using Microsoft.Extensions.Options;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries;

namespace StarWarsData.Services.AI.Citations;

/// <inheritdoc />
public sealed class CitationResolver : ICitationResolver
{
    static readonly HashSet<string> DirectSpatialTypes = new(StringComparer.Ordinal) { KgNodeTypes.System, KgNodeTypes.CelestialBody, KgNodeTypes.Sector, KgNodeTypes.Region, KgNodeTypes.TradeRoute };

    readonly IMongoCollection<GraphNode> _nodes;

    public CitationResolver(IOptions<SettingsOptions> settingsOptions, IMongoClient mongoClient)
    {
        var db = mongoClient.GetDatabase(settingsOptions.Value.DatabaseName);
        _nodes = db.GetCollection<GraphNode>(Collections.KgNodes);
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
                        Links: new CitationLinks(Wiki: null, GraphExplorer: null, GalaxyMap: null, Timeline: null, Holocron: null)
                    )
                );
                continue;
            }

            var hasGalaxyMap = DirectSpatialTypes.Contains(n.Type);

            results.Add(
                new CitationReference(
                    PageId: n.PageId,
                    Name: n.Name,
                    Kind: n.Type,
                    Continuity: n.Continuity.ToString(),
                    ImageUrl: n.ImageUrl,
                    Links: new CitationLinks(
                        Wiki: n.WikiUrl,
                        GraphExplorer: $"/graph-explorer/{n.PageId}",
                        GalaxyMap: hasGalaxyMap ? $"/galaxy-map/{n.PageId}" : null,
                        Timeline: null, // Phase 4 — needs CharacterTimelines existence check
                        Holocron: null
                    )
                )
            ); // Phase 4 — needs Holocron job existence check
        }

        return results;
    }
}
