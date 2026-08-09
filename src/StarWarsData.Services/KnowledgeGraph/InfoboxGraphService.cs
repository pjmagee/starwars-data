using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Services.KnowledgeGraph.Definitions;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders;

namespace StarWarsData.Services;

/// <summary>
/// Coordinator for the deterministic infobox-driven knowledge graph build.
/// Owns page iteration, builder dispatch, and post-processing (edge filtering,
/// dedup, lineage closures, indexes, view, label registry). Per-type extraction
/// behaviour lives in <see cref="INodeBuilder"/> implementations under the
/// <c>NodeBuilders</c> namespace — see <c>specs/035-kg-per-type-builders/spec.md</c>.
/// </summary>
public class InfoboxGraphService
{
    readonly IMongoCollection<Page> _pages;
    readonly IMongoCollection<GraphNode> _nodes;
    readonly IMongoCollection<RelationshipEdge> _edges;
    readonly IMongoCollection<RelationshipLabel> _labels;
    readonly ILogger<InfoboxGraphService> _logger;
    readonly Dictionary<string, INodeBuilder> _builders;
    readonly INodeBuilder _unknownBuilder;

    public InfoboxGraphService(IMongoClient mongoClient, IOptions<SettingsOptions> settings, ILogger<InfoboxGraphService> logger)
    {
        var db = mongoClient.GetDatabase(settings.Value.DatabaseName);
        _pages = db.GetCollection<Page>(Collections.Pages);
        _nodes = db.GetCollection<GraphNode>(Collections.KgNodes);
        _edges = db.GetCollection<RelationshipEdge>(Collections.KgEdges);
        _labels = db.GetCollection<RelationshipLabel>(Collections.KgLabels);
        _logger = logger;

        _builders = NodeBuilderRegistry.CreateBuilders();
        if (!_builders.TryGetValue(KgNodeTypes.Unknown, out var unknownBuilder))
            throw new InvalidOperationException("NodeBuilderRegistry must register a builder for KgNodeTypes.Unknown.");
        _unknownBuilder = unknownBuilder;
    }


    /// <summary>
    /// Build the knowledge graph from all pages with infoboxes.
    /// Creates GraphNode documents (with properties) and RelationshipEdge documents (for links).
    /// </summary>
    public async Task BuildGraphAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("InfoboxGraph: starting graph build from infobox data...");

        // Single pre-pass: wiki URL/title → PageId (edge target resolution) and
        // PageId → NodeType (Design-024 Phase A source × target-type relabel rules).
        var (wikiUrlToPageId, nodeTypeByPageId) = await BuildLookupsAsync(ct);
        _logger.LogInformation("InfoboxGraph: {Count} wiki URL → PageId mappings", wikiUrlToPageId.Count);
        _logger.LogInformation("InfoboxGraph: {Count} pageId → NodeType mappings", nodeTypeByPageId.Count);

        var filter = Builders<Page>.Filter.Ne(p => p.Infobox, null);
        var totalPages = await _pages.CountDocumentsAsync(filter, cancellationToken: ct);
        _logger.LogInformation("InfoboxGraph: {Total} pages with infoboxes to process", totalPages);

        var cursor = await _pages
            .Find(filter)
            .Project(
                Builders<Page>
                    .Projection.Include(p => p.PageId)
                    .Include(p => p.Title)
                    .Include(p => p.Infobox)
                    .Include(p => p.WikiUrl)
                    .Include(p => p.Continuity)
                    .Include(p => p.Realm)
                    .Include(p => p.ContentHash)
            )
            .ToCursorAsync(ct);

        var nodes = new List<GraphNode>();
        var edges = new List<RelationshipEdge>();
        var processed = 0;

        while (await cursor.MoveNextAsync(ct))
        {
            foreach (var doc in cursor.Current)
            {
                if (TryBuildContext(doc, wikiUrlToPageId, nodeTypeByPageId, out var context))
                {
                    var builder = _builders.GetValueOrDefault(context.Type, _unknownBuilder);
                    var result = builder.Build(context);
                    nodes.Add(result.Node);
                    edges.AddRange(result.Edges);
                }

                processed++;
                if (processed % 10000 == 0)
                    _logger.LogInformation("InfoboxGraph: processed {Count}/{Total} pages", processed, totalPages);
            }
        }

        _logger.LogInformation("InfoboxGraph: processed {Count} pages → {Nodes} nodes, {RawEdges} raw edges", processed, nodes.Count, edges.Count);

        // ── Post-processing: filter noise edges and enrich with target type + realm + reverse label ──
        var nodeTypeMap = nodes.ToDictionary(n => n.PageId, n => n.Type);
        var nodeRealmMap = nodes.ToDictionary(n => n.PageId, n => n.Realm);

        // label → reverse-label map sourced from FieldSemantics. Multiple semantic entries can
        // map to the same canonical label, so dedupe by label. Populated onto each edge as
        // `reverseLabel` for the kg.edges.bidir view's reverse branch.
        var reverseLabelMap = FieldSemantics
            .Relationships.Values.DistinctBy(d => d.Label, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(d => d.Label, d => d.Reverse, StringComparer.OrdinalIgnoreCase);

        var filteredEdges = new List<RelationshipEdge>(edges.Count);
        int droppedUnresolved = 0,
            droppedYear = 0,
            droppedQualifier = 0;

        foreach (var edge in edges)
        {
            if (edge.ToId == 0)
            {
                droppedUnresolved++;
                continue;
            }

            var targetType = nodeTypeMap.GetValueOrDefault(edge.ToId, "");
            edge.ToType = targetType;
            edge.ToRealm = nodeRealmMap.GetValueOrDefault(edge.ToId, Realm.Unknown);
            if (reverseLabelMap.TryGetValue(edge.Label, out var revLabel) && !string.IsNullOrEmpty(revLabel))
                edge.ReverseLabel = revLabel;

            // Drop edges to Year/Era entities — these are temporal metadata, not relationships
            if (targetType is KgNodeTypes.Year or KgNodeTypes.Era)
            {
                droppedYear++;
                continue;
            }

            // Drop qualifier edges: TitleOrPosition targets on person-relationship labels
            if (targetType == KgNodeTypes.TitleOrPosition && IsPersonRelationshipLabel(edge.Label))
            {
                droppedQualifier++;
                continue;
            }

            // Drop ForcePower/LightsaberForm targets on person-relationship labels
            if (targetType is KgNodeTypes.ForcePower or KgNodeTypes.LightsaberForm && IsPersonRelationshipLabel(edge.Label))
            {
                droppedQualifier++;
                continue;
            }

            filteredEdges.Add(edge);
        }

        _logger.LogInformation(
            "InfoboxGraph: edge filtering — dropped {Unresolved} unresolved, {Year} temporal, {Qualifier} qualifier → {Clean} clean edges",
            droppedUnresolved,
            droppedYear,
            droppedQualifier,
            filteredEdges.Count
        );

        // ── Derive temporal bounds on edges from node lifecycles ──
        var nodeLifecycleMap = nodes.Where(n => n.StartYear.HasValue).ToDictionary(n => n.PageId, n => (start: n.StartYear, end: n.EndYear));

        int derivedCount = 0;
        foreach (var edge in filteredEdges)
        {
            if (edge.FromYear.HasValue)
                continue;

            var hasSrc = nodeLifecycleMap.TryGetValue(edge.FromId, out var src);
            var hasTgt = nodeLifecycleMap.TryGetValue(edge.ToId, out var tgt);

            var derived = false;

            if (hasSrc && hasTgt && src.start.HasValue && tgt.start.HasValue)
            {
                var from = Math.Max(src.start.Value, tgt.start.Value);
                var to = (src.end, tgt.end) switch
                {
                    (int a, int b) => Math.Min(a, b),
                    (int a, null) => a,
                    (null, int b) => b,
                    _ => (int?)null,
                };

                if (to is null || from <= to)
                {
                    edge.FromYear = from;
                    edge.ToYear = to;
                    derivedCount++;
                    derived = true;
                }
            }
            else if (hasSrc && src.start.HasValue && !hasTgt)
            {
                edge.FromYear = src.start.Value;
                edge.ToYear = src.end;
                derivedCount++;
                derived = true;
            }
            else if (hasTgt && tgt.start.HasValue && !hasSrc)
            {
                edge.FromYear = tgt.start.Value;
                edge.ToYear = tgt.end;
                derivedCount++;
                derived = true;
            }

            // Tag provenance (Design-021). Lifecycle-fallback bounds are soft upper bounds —
            // Holocron's FillGap is allowed to refine these with chunk-cited evidence, but
            // not infobox-supplied ones. NodeBuilderBase already stamps Infobox at write time;
            // here we stamp Lifecycle on the fallback path. Materialise Meta if it was null.
            if (derived)
            {
                edge.Meta ??= new EdgeMeta();
                edge.Meta.BoundsSource = EdgeBoundsSource.Lifecycle;
            }
        }

        _logger.LogInformation(
            "InfoboxGraph: derived temporal bounds on {Derived} edges (from {Explicit} explicit + node lifecycle overlap)",
            derivedCount,
            filteredEdges.Count(e => e.FromYear.HasValue) - derivedCount
        );

        // ── Dedup: a single infobox can emit multiple edges with the same (fromId, toId, label)
        // when two fields map to the same canonical label (e.g. "Masters" and "Teacher" both →
        // apprentice_of → same target). Collapse these to one, preferring the richer instance
        // (explicit temporal bounds > derived bounds > no bounds; then higher weight). This
        // matches the unique constraint ix_fromId_toId_label on kg.edges and prevents the
        // downstream QueryGraphAsync dedupe from having to do the same work on every read.
        var preDedupe = filteredEdges.Count;
        filteredEdges = filteredEdges
            .OrderByDescending(e => e.FromYear.HasValue ? 1 : 0)
            .ThenByDescending(e => e.Meta is not null ? 1 : 0)
            .ThenByDescending(e => e.Weight)
            .DistinctBy(e => (e.FromId, e.ToId, e.Label))
            .ToList();
        var deduped = preDedupe - filteredEdges.Count;
        if (deduped > 0)
            _logger.LogInformation("InfoboxGraph: deduped {Dropped} duplicate edges ({Pre} → {Post})", deduped, preDedupe, filteredEdges.Count);

        // ── Hierarchy helpers: precompute transitive closures for tree/DAG-shaped labels.
        // Each registered lineage walks edges of a single label in a single direction from
        // every seed node and stores the ordered closure on the seed as `lineages.<key>`.
        // Cycle-safe: the BFS uses a visited set, so the two known apprentice_of cycles
        // (characters who trained each other) are handled transparently.
        // See ADR-003 Gap 1, Design-008, and HierarchyRegistry.
        ComputeLineageClosures(nodes, filteredEdges);

        // Write to MongoDB
        _logger.LogInformation("InfoboxGraph: writing nodes...");
        if (nodes.Count > 0)
        {
            await _nodes.DeleteManyAsync(FilterDefinition<GraphNode>.Empty, ct);
            await _nodes.InsertManyAsync(nodes, new InsertManyOptions { IsOrdered = false }, ct);
        }

        _logger.LogInformation("InfoboxGraph: writing edges...");
        if (filteredEdges.Count > 0)
        {
            await _edges.DeleteManyAsync(FilterDefinition<RelationshipEdge>.Empty, ct);
            await _edges.InsertManyAsync(filteredEdges, new InsertManyOptions { IsOrdered = false }, ct);
        }

        // Create indexes
        await _nodes.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<GraphNode>(Builders<GraphNode>.IndexKeys.Ascending(n => n.Type)),
                new CreateIndexModel<GraphNode>(Builders<GraphNode>.IndexKeys.Ascending(n => n.Name)),
                new CreateIndexModel<GraphNode>(Builders<GraphNode>.IndexKeys.Ascending(n => n.Continuity)),
                new CreateIndexModel<GraphNode>(
                    Builders<GraphNode>.IndexKeys.Ascending("temporalFacets.semantic").Ascending("temporalFacets.year"),
                    new CreateIndexOptions { Name = "ix_temporal_semantic_year" }
                ),
                new CreateIndexModel<GraphNode>(
                    Builders<GraphNode>.IndexKeys.Ascending("temporalFacets.calendar").Ascending("temporalFacets.year"),
                    new CreateIndexOptions { Name = "ix_temporal_calendar_year" }
                ),
                // Wildcard index over all lineage closure subdocuments. Powers O(1) membership
                // queries like {"lineages.apprentice_of": targetId} without requiring a dedicated
                // index per HierarchyRegistry entry. See ADR-003 Gap 1 and Design-008.
                new CreateIndexModel<GraphNode>(Builders<GraphNode>.IndexKeys.Ascending("lineages.$**"), new CreateIndexOptions { Name = "ix_lineages_wildcard" }),
            ],
            ct
        );

        // ── Authoritative kg.edges index set ──
        // This is the single site that creates indexes on kg.edges — Phase 5 is a
        // full delete+insert so we re-assert the index set on every rebuild.
        // Singleton prefixes (fromId, toId) are intentionally omitted: they are
        // covered by the compound (fromId, label) / (toId, label) indexes as leading-key prefixes.
        await _edges.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<RelationshipEdge>(
                    Builders<RelationshipEdge>.IndexKeys.Ascending(e => e.FromId).Ascending(e => e.ToId).Ascending(e => e.Label),
                    new CreateIndexOptions { Name = "ix_fromId_toId_label", Unique = true }
                ),
                new CreateIndexModel<RelationshipEdge>(Builders<RelationshipEdge>.IndexKeys.Ascending(e => e.FromId).Ascending(e => e.Label), new CreateIndexOptions { Name = "ix_fromId_label" }),
                new CreateIndexModel<RelationshipEdge>(Builders<RelationshipEdge>.IndexKeys.Ascending(e => e.ToId).Ascending(e => e.Label), new CreateIndexOptions { Name = "ix_toId_label" }),
                new CreateIndexModel<RelationshipEdge>(Builders<RelationshipEdge>.IndexKeys.Ascending(e => e.Label), new CreateIndexOptions { Name = "ix_label" }),
                new CreateIndexModel<RelationshipEdge>(Builders<RelationshipEdge>.IndexKeys.Ascending(e => e.Continuity), new CreateIndexOptions { Name = "ix_continuity" }),
                new CreateIndexModel<RelationshipEdge>(Builders<RelationshipEdge>.IndexKeys.Ascending(e => e.SourcePageId), new CreateIndexOptions { Name = "ix_sourcePageId" }),
            ],
            ct
        );

        await BuildLabelRegistryAsync(ct);
        await EnsureBidirectionalEdgesViewAsync(ct);

        _logger.LogInformation("InfoboxGraph: complete. {Nodes} nodes, {Edges} edges (from {RawEdges} raw), indexes created.", nodes.Count, filteredEdges.Count, edges.Count);
    }

    /// <summary>
    /// Project a raw page document into a <see cref="NodeBuilderContext"/>.
    /// Returns false (and a default context) for pages that cannot be processed
    /// — currently never happens, but keeps the dispatch loop linear.
    /// </summary>
    static bool TryBuildContext(BsonDocument doc, IReadOnlyDictionary<string, int> wikiUrlToPageId, IReadOnlyDictionary<int, string> nodeTypeByPageId, out NodeBuilderContext context)
    {
        var pageId = doc[MongoFields.Id].AsInt32;
        var title = doc[PageBsonFields.Title].AsString;
        var continuity = doc.Contains(PageBsonFields.Continuity)
            ? Enum.TryParse<Continuity>(doc[PageBsonFields.Continuity].AsString, out var c)
                ? c
                : Continuity.Unknown
            : Continuity.Unknown;
        var realm = doc.Contains(PageBsonFields.Realm)
            ? Enum.TryParse<Realm>(doc[PageBsonFields.Realm].AsString, out var u)
                ? u
                : Realm.Unknown
            : Realm.Unknown;
        var contentHash = doc.Contains(PageBsonFields.ContentHash) && !doc[PageBsonFields.ContentHash].IsBsonNull ? doc[PageBsonFields.ContentHash].AsString : null;
        var wikiUrl = doc.Contains(PageBsonFields.WikiUrl) ? doc[PageBsonFields.WikiUrl].AsString : null;

        var infoboxDoc = doc[PageBsonFields.Infobox].AsBsonDocument;
        var template = infoboxDoc.Contains(InfoboxBsonFields.Template) && !infoboxDoc[InfoboxBsonFields.Template].IsBsonNull ? infoboxDoc[InfoboxBsonFields.Template].AsString : null;
        var imageUrl = infoboxDoc.Contains(InfoboxBsonFields.ImageUrl) && !infoboxDoc[InfoboxBsonFields.ImageUrl].IsBsonNull ? infoboxDoc[InfoboxBsonFields.ImageUrl].AsString : null;

        // Colon-less templates fall back to Unknown (dispatch-safe). The lookup
        // pre-pass uses the raw string as fallback so historical type maps stay intact.
        var type = template is not null ? ParseTemplateType(template, fallbackWhenNoColon: KgNodeTypes.Unknown) : KgNodeTypes.Unknown;

        var dataItems = infoboxDoc.Contains(InfoboxBsonFields.Data) && infoboxDoc[InfoboxBsonFields.Data].IsBsonArray ? infoboxDoc[InfoboxBsonFields.Data].AsBsonArray : new BsonArray();

        var definition = InfoboxDefinitionRegistry.ForTemplate(type);

        context = new NodeBuilderContext(
            PageId: pageId,
            Title: title,
            Type: type,
            Continuity: continuity,
            Realm: realm,
            ContentHash: contentHash,
            WikiUrl: wikiUrl,
            ImageUrl: imageUrl,
            DataItems: dataItems,
            Definition: definition,
            WikiUrlToPageId: wikiUrlToPageId,
            NodeTypeByPageId: nodeTypeByPageId
        );
        return true;
    }

    /// <summary>
    /// Compute transitive closures for every <see cref="HierarchyRegistry.Lineages"/> entry and
    /// embed them on the matching nodes as <c>Lineages[lineageKey]</c>. Cycle-safe via BFS with
    /// a visited set — the two known <c>apprentice_of</c> cycles on dev are handled transparently.
    ///
    /// Runs after edge dedup so the adjacency maps don't carry duplicate fan-out that would
    /// cause redundant BFS work. Runs before <c>_nodes.InsertManyAsync</c> so the lineage data
    /// lands in the initial insert rather than requiring a follow-up update pass.
    /// </summary>
    void ComputeLineageClosures(List<GraphNode> nodes, List<RelationshipEdge> filteredEdges)
    {
        var nodeMap = nodes.ToDictionary(n => n.PageId);

        foreach (var lineage in HierarchyRegistry.Lineages)
        {
            // Build a one-hop adjacency map for this lineage: seed → next-hop neighbours.
            // Forward direction: adjacency keyed by fromId, value = toIds.
            // Reverse direction: adjacency keyed by toId, value = fromIds (so the walk proceeds
            // against the stored edge direction).
            var adjacency = new Dictionary<int, List<int>>();
            foreach (var edge in filteredEdges)
            {
                if (!string.Equals(edge.Label, lineage.Label, StringComparison.OrdinalIgnoreCase))
                    continue;

                var seed = lineage.Direction == HierarchyRegistry.LineageDirection.Forward ? edge.FromId : edge.ToId;
                var next = lineage.Direction == HierarchyRegistry.LineageDirection.Forward ? edge.ToId : edge.FromId;
                if (!adjacency.TryGetValue(seed, out var list))
                {
                    list = [];
                    adjacency[seed] = list;
                }
                list.Add(next);
            }

            if (adjacency.Count == 0)
                continue;

            var populated = 0;
            foreach (var (seedId, _) in adjacency)
            {
                var closure = new List<int>();
                var visited = new HashSet<int> { seedId };
                var queue = new Queue<int>();
                queue.Enqueue(seedId);

                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    if (!adjacency.TryGetValue(current, out var neighbours))
                        continue;
                    foreach (var neighbour in neighbours)
                    {
                        if (visited.Add(neighbour))
                        {
                            closure.Add(neighbour);
                            queue.Enqueue(neighbour);
                        }
                    }
                }

                if (closure.Count > 0 && nodeMap.TryGetValue(seedId, out var node))
                {
                    node.Lineages[lineage.LineageKey] = closure;
                    populated++;
                }
            }

            _logger.LogInformation(
                "InfoboxGraph: lineage {LineageKey} ({Label}/{Direction}) — populated {Populated} nodes, {Edges} edges in adjacency",
                lineage.LineageKey,
                lineage.Label,
                lineage.Direction,
                populated,
                adjacency.Values.Sum(v => v.Count)
            );
        }
    }

    /// <summary>
    /// Create (or replace) the <c>kg.edges.bidir</c> view. Forward branch passes through every
    /// edge as-stored with <c>direction: "forward"</c>. Reverse branch unions a second copy of
    /// <c>kg.edges</c> with <c>fromId</c>/<c>toId</c>, names, types, and realms flipped, and the
    /// label replaced by its denormalized <c>reverseLabel</c> — edges with no reverse are dropped
    /// from the reverse branch so the view never surfaces ambiguous labels.
    /// </summary>
    public async Task EnsureBidirectionalEdgesViewAsync(CancellationToken ct = default)
    {
        var db = _edges.Database;
        const string ViewName = "kg.edges.bidir";

        try
        {
            await db.DropCollectionAsync(ViewName, ct);
        }
        catch (MongoCommandException)
        {
            // View didn't exist — fine.
        }

        var pipeline = new BsonArray
        {
            new BsonDocument("$addFields", new BsonDocument("direction", "forward")),
            new BsonDocument(
                "$unionWith",
                new BsonDocument
                {
                    { "coll", Collections.KgEdges },
                    {
                        "pipeline",
                        new BsonArray
                        {
                            new BsonDocument(
                                "$match",
                                new BsonDocument(RelationshipEdgeBsonFields.ReverseLabel, new BsonDocument("$exists", true).Add("$nin", new BsonArray { BsonNull.Value, string.Empty }))
                            ),
                            new BsonDocument(
                                "$project",
                                new BsonDocument
                                {
                                    { MongoFields.Id, 1 },
                                    { RelationshipEdgeBsonFields.FromId, "$" + RelationshipEdgeBsonFields.ToId },
                                    { RelationshipEdgeBsonFields.ToId, "$" + RelationshipEdgeBsonFields.FromId },
                                    { RelationshipEdgeBsonFields.FromName, "$" + RelationshipEdgeBsonFields.ToName },
                                    { RelationshipEdgeBsonFields.ToName, "$" + RelationshipEdgeBsonFields.FromName },
                                    { RelationshipEdgeBsonFields.FromType, "$" + RelationshipEdgeBsonFields.ToType },
                                    { RelationshipEdgeBsonFields.ToType, "$" + RelationshipEdgeBsonFields.FromType },
                                    { RelationshipEdgeBsonFields.FromRealm, "$" + RelationshipEdgeBsonFields.ToRealm },
                                    { RelationshipEdgeBsonFields.ToRealm, "$" + RelationshipEdgeBsonFields.FromRealm },
                                    { RelationshipEdgeBsonFields.Label, "$" + RelationshipEdgeBsonFields.ReverseLabel },
                                    { RelationshipEdgeBsonFields.Weight, 1 },
                                    { RelationshipEdgeBsonFields.Evidence, 1 },
                                    { RelationshipEdgeBsonFields.Continuity, 1 },
                                    { RelationshipEdgeBsonFields.FromYear, 1 },
                                    { RelationshipEdgeBsonFields.ToYear, 1 },
                                    { RelationshipEdgeBsonFields.SourcePageId, 1 },
                                    { "direction", "reverse" },
                                }
                            ),
                        }
                    },
                }
            ),
        };

        var createViewCommand = new BsonDocument
        {
            { "create", ViewName },
            { "viewOn", Collections.KgEdges },
            { "pipeline", pipeline },
        };

        await db.RunCommandAsync<BsonDocument>(createViewCommand, cancellationToken: ct);
        _logger.LogInformation("InfoboxGraph: created view {ViewName} (forward + reverse branches)", ViewName);
    }

    /// <summary>
    /// Rebuild the <c>kg.labels</c> registry as a materialized view over <c>kg.edges</c>.
    /// Seeds each label's <c>reverse</c>/<c>description</c>/<c>fromTypes</c>/<c>toTypes</c> from
    /// <see cref="FieldSemantics.Relationships"/>, then overlays the observed usage count and
    /// the actually-observed from/to type sets via a single aggregation pipeline.
    /// </summary>
    public async Task BuildLabelRegistryAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("InfoboxGraph: rebuilding kg.labels registry...");

        var seed = FieldSemantics.Relationships.Values.DistinctBy(d => d.Label, StringComparer.OrdinalIgnoreCase).ToDictionary(d => d.Label, d => d, StringComparer.OrdinalIgnoreCase);

        var observed = await _edges
            .Aggregate()
            .Group(
                new BsonDocument
                {
                    { "_id", "$label" },
                    { "usageCount", new BsonDocument("$sum", 1) },
                    { "fromTypes", new BsonDocument("$addToSet", "$fromType") },
                    { "toTypes", new BsonDocument("$addToSet", "$toType") },
                }
            )
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var labelDocs = new List<RelationshipLabel>(capacity: observed.Count);
        foreach (var row in observed)
        {
            var label = row["_id"].AsString;
            seed.TryGetValue(label, out var def);

            labelDocs.Add(
                new RelationshipLabel
                {
                    Label = label,
                    Reverse = def?.Reverse ?? string.Empty,
                    Description = def?.Description ?? string.Empty,
                    FromTypes = [.. row["fromTypes"].AsBsonArray.Select(v => v.AsString).Where(s => !string.IsNullOrEmpty(s)).OrderBy(s => s)],
                    ToTypes = [.. row["toTypes"].AsBsonArray.Select(v => v.AsString).Where(s => !string.IsNullOrEmpty(s)).OrderBy(s => s)],
                    UsageCount = row["usageCount"].AsInt32,
                    CreatedAt = now,
                }
            );
        }

        foreach (var def in seed.Values)
        {
            if (labelDocs.Any(l => l.Label.Equals(def.Label, StringComparison.OrdinalIgnoreCase)))
                continue;
            labelDocs.Add(
                new RelationshipLabel
                {
                    Label = def.Label,
                    Reverse = def.Reverse,
                    Description = def.Description,
                    FromTypes = [],
                    ToTypes = [.. def.ExpectedTargetTypes.OrderBy(s => s)],
                    UsageCount = 0,
                    CreatedAt = now,
                }
            );
        }

        await _labels.DeleteManyAsync(FilterDefinition<RelationshipLabel>.Empty, ct);
        if (labelDocs.Count > 0)
            await _labels.InsertManyAsync(labelDocs, new InsertManyOptions { IsOrdered = false }, ct);

        await _labels.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<RelationshipLabel>(Builders<RelationshipLabel>.IndexKeys.Descending(l => l.UsageCount), new CreateIndexOptions { Name = "ix_usageCount" }),
                new CreateIndexModel<RelationshipLabel>(Builders<RelationshipLabel>.IndexKeys.Ascending(l => l.FromTypes), new CreateIndexOptions { Name = "ix_fromTypes" }),
                new CreateIndexModel<RelationshipLabel>(Builders<RelationshipLabel>.IndexKeys.Ascending(l => l.ToTypes), new CreateIndexOptions { Name = "ix_toTypes" }),
            ],
            ct
        );

        _logger.LogInformation("InfoboxGraph: kg.labels registry has {Count} labels ({Observed} observed, {SeedOnly} seed-only)", labelDocs.Count, observed.Count, labelDocs.Count - observed.Count);
    }

    /// <summary>
    /// Extract the KG node type from an infobox template string.
    /// Templates are typically <c>Template:Battle</c>; the type is the suffix after
    /// the last colon. When no colon is present, <paramref name="fallbackWhenNoColon"/>
    /// is returned — callers that want corpus-parity with the historical pageId→type
    /// lookup pass the raw template; callers that want dispatch-safe defaults pass
    /// <see cref="KgNodeTypes.Unknown"/>.
    /// </summary>
    internal static string ParseTemplateType(string template, string fallbackWhenNoColon)
    {
        var idx = template.LastIndexOf(':');
        return idx >= 0 ? template[(idx + 1)..] : fallbackWhenNoColon;
    }

    /// <summary>
    /// Single pre-pass over all pages building both lookups the graph build needs:
    /// <list type="bullet">
    ///   <item><c>urlToPageId</c> — wiki URL AND title → PageId for link-target resolution
    ///   (always filled when the field is present).</item>
    ///   <item><c>typeByPageId</c> — PageId → KG node type, filled only when an infobox
    ///   template is present. Used by per-type <c>OnFinalize</c> overrides (Design-024
    ///   Phase A) for source × target-type relabel rules.</item>
    /// </list>
    /// Colon-less templates keep the raw string in <c>typeByPageId</c> (historical
    /// lookup behaviour); <see cref="TryBuildContext"/> defaults those to Unknown.
    /// </summary>
    async Task<(Dictionary<string, int> UrlToPageId, Dictionary<int, string> TypeByPageId)> BuildLookupsAsync(CancellationToken ct)
    {
        var urlToPageId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var typeByPageId = new Dictionary<int, string>();

        var cursor = await _pages
            .Find(FilterDefinition<Page>.Empty)
            .Project(
                Builders<Page>
                    .Projection.Include(p => p.PageId)
                    .Include(p => p.Title)
                    .Include(p => p.WikiUrl)
                    .Include(PageBsonFields.InfoboxTemplate)
            )
            .ToCursorAsync(ct);

        while (await cursor.MoveNextAsync(ct))
        {
            foreach (var doc in cursor.Current)
            {
                var pageId = doc[MongoFields.Id].AsInt32;
                var wikiUrl = doc.Contains(PageBsonFields.WikiUrl) ? doc[PageBsonFields.WikiUrl].AsString : null;
                var title = doc.Contains(PageBsonFields.Title) ? doc[PageBsonFields.Title].AsString : null;

                if (wikiUrl is not null)
                    urlToPageId.TryAdd(wikiUrl, pageId);

                if (title is not null)
                    urlToPageId.TryAdd(title, pageId);

                if (!doc.Contains(PageBsonFields.Infobox) || doc[PageBsonFields.Infobox].IsBsonNull)
                    continue;

                var infoboxDoc = doc[PageBsonFields.Infobox].AsBsonDocument;
                var template =
                    infoboxDoc.Contains(InfoboxBsonFields.Template) && !infoboxDoc[InfoboxBsonFields.Template].IsBsonNull
                        ? infoboxDoc[InfoboxBsonFields.Template].AsString
                        : null;
                if (template is null)
                    continue;

                // Historical lookup kept the raw string for colon-less templates.
                var type = ParseTemplateType(template, fallbackWhenNoColon: template);
                if (!string.IsNullOrEmpty(type))
                    typeByPageId[pageId] = type;
            }
        }

        return (urlToPageId, typeByPageId);
    }

    /// <summary>
    /// True if an edge label is expected to target a person/character.
    /// Derived from the registered <see cref="LabelDefinition.ExpectedTargetTypes"/>
    /// — no hardcoded list. Used to filter qualifier noise (e.g. apprentice_of → "Jedi Master"
    /// instead of the actual person).
    /// </summary>
    static bool IsPersonRelationshipLabel(string label) => InfoboxDefinitionRegistry.EdgeTargetsPerson(label);
}
