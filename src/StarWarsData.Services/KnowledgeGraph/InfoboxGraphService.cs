using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Services.KnowledgeGraph.Definitions;

namespace StarWarsData.Services;

/// <summary>
/// Builds the knowledge graph deterministically from infobox data.
/// No LLM calls — classifies each infobox field as a property (scalar)
/// or relationship (link to another entity) based on field name and link presence.
/// </summary>
public class InfoboxGraphService(IMongoClient mongoClient, IOptions<SettingsOptions> settings, ILogger<InfoboxGraphService> logger)
{
    readonly IMongoCollection<Page> _pages = mongoClient.GetDatabase(settings.Value.DatabaseName).GetCollection<Page>(Collections.Pages);
    readonly IMongoCollection<GraphNode> _nodes = mongoClient.GetDatabase(settings.Value.DatabaseName).GetCollection<GraphNode>(Collections.KgNodes);
    readonly IMongoCollection<RelationshipEdge> _edges = mongoClient.GetDatabase(settings.Value.DatabaseName).GetCollection<RelationshipEdge>(Collections.KgEdges);
    readonly IMongoCollection<RelationshipLabel> _labels = mongoClient.GetDatabase(settings.Value.DatabaseName).GetCollection<RelationshipLabel>(Collections.KgLabels);

    // ── Field classification ──
    // All infobox field metadata (properties, relationships, temporal facets) lives in
    // StarWarsData.Services.KnowledgeGraph.Definitions.FieldCategories/* and is merged
    // via InfoboxDefinitionRegistry. The service queries the registry for each field label
    // rather than maintaining its own hashsets. This keeps the classification logic thin
    // and puts field provenance (which category a field belongs to) in the file system.

    /// <summary>
    /// Build the knowledge graph from all pages with infoboxes.
    /// Creates GraphNode documents (with properties) and RelationshipEdge documents (for links).
    /// </summary>
    public async Task BuildGraphAsync(CancellationToken ct = default)
    {
        logger.LogInformation("InfoboxGraph: starting graph build from infobox data...");

        // Build a PageId lookup from wiki URLs so we can resolve link targets
        var wikiUrlToPageId = await BuildWikiUrlLookupAsync(ct);
        logger.LogInformation("InfoboxGraph: {Count} wiki URL → PageId mappings", wikiUrlToPageId.Count);

        // Process all pages with infoboxes
        var filter = Builders<Page>.Filter.Ne(p => p.Infobox, null);
        var totalPages = await _pages.CountDocumentsAsync(filter, cancellationToken: ct);
        logger.LogInformation("InfoboxGraph: {Total} pages with infoboxes to process", totalPages);

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

                // Extract type from template URL
                var type = KgNodeTypes.Unknown;
                if (template is not null)
                {
                    var idx = template.LastIndexOf(':');
                    if (idx >= 0)
                        type = template[(idx + 1)..];
                }

                // Parse infobox data items
                var dataItems = infoboxDoc.Contains(InfoboxBsonFields.Data) && infoboxDoc[InfoboxBsonFields.Data].IsBsonArray ? infoboxDoc[InfoboxBsonFields.Data].AsBsonArray : new BsonArray();

                var properties = new Dictionary<string, List<string>>();
                var facets = new List<TemporalFacet>();

                // Load the per-template definition once per page. This is the template-scoped
                // view: only fields that actually appear on this template are classified.
                var definition = InfoboxDefinitionRegistry.ForTemplate(type);

                foreach (var item in dataItems)
                {
                    if (item is not BsonDocument itemDoc)
                        continue;
                    var label = itemDoc.Contains(InfoboxBsonFields.Label) ? itemDoc[InfoboxBsonFields.Label].AsString : null;
                    if (label is null)
                        continue;

                    var values =
                        itemDoc.Contains(InfoboxBsonFields.Values) && itemDoc[InfoboxBsonFields.Values].IsBsonArray
                            ? itemDoc[InfoboxBsonFields.Values].AsBsonArray.Select(v => v.AsString).ToList()
                            : [];
                    var links =
                        itemDoc.Contains(InfoboxBsonFields.Links) && itemDoc[InfoboxBsonFields.Links].IsBsonArray
                            ? itemDoc[InfoboxBsonFields.Links].AsBsonArray.Where(l => l is BsonDocument).Select(l => l.AsBsonDocument).ToList()
                            : [];

                    // Extract temporal facets from known temporal fields for this template.
                    // Links inside a temporal value are contextual (place of death, cause of
                    // destruction, killer, etc.) — they are NOT semantic relationships of the
                    // field name. E.g. Character.Died = "4 ABY, Death Star II over Endor system"
                    // produces a lifespan.end facet but must NOT produce a "died → Death Star II"
                    // edge. So once a field is classified as temporal, we skip the relationship
                    // classification entirely.
                    if (definition.TemporalFields.TryGetValue(label, out var temporalDef))
                    {
                        foreach (var val in values)
                        {
                            if (string.IsNullOrWhiteSpace(val))
                                continue;

                            if (temporalDef.IsRange)
                            {
                                // Range field: extract every BBY/ABY year in the value
                                // and emit a facet per year. One year → ".point",
                                // two years → ".start" + ".end", 3+ → start/end/mid.
                                var years = ParseAllGalacticYears(val);
                                if (years.Count == 0)
                                {
                                    facets.Add(
                                        new TemporalFacet
                                        {
                                            Field = label,
                                            Semantic = $"{temporalDef.Semantic}.point",
                                            Calendar = "galactic",
                                            Year = null,
                                            Text = val,
                                        }
                                    );
                                }
                                else if (years.Count == 1)
                                {
                                    facets.Add(
                                        new TemporalFacet
                                        {
                                            Field = label,
                                            Semantic = $"{temporalDef.Semantic}.point",
                                            Calendar = "galactic",
                                            Year = years[0],
                                            Text = val,
                                        }
                                    );
                                }
                                else
                                {
                                    for (var i = 0; i < years.Count; i++)
                                    {
                                        var role =
                                            i == 0 ? "start"
                                            : i == years.Count - 1 ? "end"
                                            : "mid";
                                        facets.Add(
                                            new TemporalFacet
                                            {
                                                Field = label,
                                                Semantic = $"{temporalDef.Semantic}.{role}",
                                                Calendar = "galactic",
                                                Year = years[i],
                                                Text = val,
                                            }
                                        );
                                    }
                                }
                            }
                            else
                            {
                                var (calendar, year) = DetectCalendarAndParse(val, temporalDef.Calendar);
                                facets.Add(
                                    new TemporalFacet
                                    {
                                        Field = label,
                                        Semantic = temporalDef.Semantic,
                                        Calendar = calendar,
                                        Year = year,
                                        Text = val,
                                    }
                                );
                            }
                        }
                        // Also store the raw text as a property for display
                        if (values.Count > 0)
                            properties[label] = values;
                        // Do NOT fall through to relationship classification.
                        continue;
                    }

                    // Classify: property or relationship? Scoped to this template.
                    var hasLabelDef = definition.Relationships.TryGetValue(label, out var labelDef);
                    if (definition.Properties.Contains(label))
                    {
                        // Store as scalar property
                        if (values.Count > 0)
                            properties[label] = values;
                    }
                    else if (!hasLabelDef && links.Count == 0)
                    {
                        // Fallthrough: field has no property definition, no relationship
                        // definition, no temporal classification, and no links. Preserve
                        // the raw infobox text so it's not silently lost — the KG is the
                        // single runtime source of truth, so anything raw.pages has must
                        // land on the node. See eng/design/013-kg-property-edge-duality.md.
                        if (values.Count > 0)
                            properties[label] = values;
                    }
                    else if (hasLabelDef || links.Count > 0)
                    {
                        var edgeLabel = labelDef?.Label ?? NormaliseLabel(label);
                        var weight = labelDef?.Weight ?? 0.8;

                        // Build link lookup: content text → (href, content)
                        var linkLookup = new Dictionary<string, BsonDocument>(StringComparer.OrdinalIgnoreCase);
                        foreach (var link in links)
                        {
                            var content = link.Contains(InfoboxBsonFields.Content) ? link[InfoboxBsonFields.Content].AsString : null;
                            if (content is not null)
                                linkLookup.TryAdd(content, link);
                        }

                        // Match each Value to its primary link (text before parenthetical)
                        var primaryLinks = ExtractPrimaryLinks(values, linkLookup);

                        if (primaryLinks.Count > 0)
                        {
                            // Create edges only for primary entities from Values
                            foreach (var pl in primaryLinks)
                            {
                                var href = pl.Link.Contains(InfoboxBsonFields.Href) ? pl.Link[InfoboxBsonFields.Href].AsString : null;
                                var content = pl.Link.Contains(InfoboxBsonFields.Content) ? pl.Link[InfoboxBsonFields.Content].AsString : null;
                                if (href is null || content is null)
                                    continue;

                                var targetPageId = ResolveLinkTarget(href, content, wikiUrlToPageId);

                                edges.Add(
                                    new RelationshipEdge
                                    {
                                        FromId = pageId,
                                        FromName = title,
                                        FromType = type,
                                        FromRealm = realm,
                                        ToId = targetPageId,
                                        ToName = content,
                                        ToType = "",
                                        Label = edgeLabel,
                                        Weight = weight,
                                        Evidence = $"Infobox field '{label}'",
                                        SourcePageId = pageId,
                                        Continuity = continuity,
                                        FromYear = pl.FromYear,
                                        ToYear = pl.ToYear,
                                        Meta =
                                            pl.Qualifier is null && pl.RawValue == content
                                                ? null
                                                : new EdgeMeta
                                                {
                                                    Qualifier = pl.Qualifier,
                                                    RawValue = pl.RawValue != content ? pl.RawValue : null,
                                                    Order = pl.Order,
                                                },
                                    }
                                );
                            }
                        }
                        else
                        {
                            // Fallback: no Values or no matches — create edges for all links
                            foreach (var link in links)
                            {
                                var href = link.Contains(InfoboxBsonFields.Href) ? link[InfoboxBsonFields.Href].AsString : null;
                                var content = link.Contains(InfoboxBsonFields.Content) ? link[InfoboxBsonFields.Content].AsString : null;
                                if (href is null || content is null)
                                    continue;

                                var targetPageId = ResolveLinkTarget(href, content, wikiUrlToPageId);
                                edges.Add(
                                    new RelationshipEdge
                                    {
                                        FromId = pageId,
                                        FromName = title,
                                        FromType = type,
                                        FromRealm = realm,
                                        ToId = targetPageId,
                                        ToName = content,
                                        ToType = "",
                                        Label = edgeLabel,
                                        Weight = weight * 0.8,
                                        Evidence = $"Infobox field '{label}' (fallback)",
                                        SourcePageId = pageId,
                                        Continuity = continuity,
                                    }
                                );
                            }
                        }

                        // If no links at all, store as property
                        if (links.Count == 0 && values.Count > 0)
                            properties[label] = values;

                        // Trade routes: store ordered pageId sequences so consumers can
                        // reconstruct the geographic route without name-based joins.
                        // The property key is "{label}Ids" (e.g. "Other objectsIds").
                        if (type == KgNodeTypes.TradeRoute && primaryLinks.Count > 0)
                        {
                            var orderedIds = primaryLinks
                                .OrderBy(pl => pl.Order)
                                .Select(pl =>
                                {
                                    var href = pl.Link.Contains(InfoboxBsonFields.Href) ? pl.Link[InfoboxBsonFields.Href].AsString : null;
                                    var content = pl.Link.Contains(InfoboxBsonFields.Content) ? pl.Link[InfoboxBsonFields.Content].AsString : null;
                                    return href is not null && content is not null ? ResolveLinkTarget(href, content, wikiUrlToPageId) : 0;
                                })
                                .Where(id => id > 0)
                                .Select(id => id.ToString())
                                .ToList();
                            if (orderedIds.Count > 0)
                                properties[$"{label}Ids"] = orderedIds;
                        }
                    }
                }

                // Assign lifecycle ordering within each semantic prefix
                AssignFacetOrder(facets);

                // Compute envelope from facets for fast range queries
                var startFacets = facets.Where(f => f.Year.HasValue && (f.Semantic.EndsWith(".start") || f.Semantic.EndsWith(".point") || f.Semantic.EndsWith(".release"))).ToList();
                var endFacets = facets.Where(f => f.Year.HasValue && (f.Semantic.EndsWith(".end") || f.Semantic.EndsWith(".point"))).ToList();

                var startYear = startFacets.Count > 0 ? startFacets.Min(f => f.Year!.Value) : (int?)null;
                var endYear = endFacets.Count > 0 ? endFacets.Max(f => f.Year!.Value) : (int?)null;

                // For backward compat: keep startDateText/endDateText from first start/end facet
                var startDateText = startFacets.FirstOrDefault()?.Text;
                var endDateText = endFacets.FirstOrDefault()?.Text;

                nodes.Add(
                    new GraphNode
                    {
                        PageId = pageId,
                        Name = title,
                        Type = type,
                        Continuity = continuity,
                        Realm = realm,
                        Properties = properties,
                        ImageUrl = imageUrl,
                        WikiUrl = wikiUrl,
                        StartYear = startYear,
                        EndYear = endYear,
                        StartDateText = startDateText,
                        EndDateText = endDateText,
                        TemporalFacets = facets,
                        ContentHash = contentHash,
                        ProcessedAt = DateTime.UtcNow,
                    }
                );

                processed++;
                if (processed % 10000 == 0)
                    logger.LogInformation("InfoboxGraph: processed {Count}/{Total} pages", processed, totalPages);
            }
        }

        logger.LogInformation("InfoboxGraph: processed {Count} pages → {Nodes} nodes, {RawEdges} raw edges", processed, nodes.Count, edges.Count);

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
            // Drop unresolved targets (link didn't match any page)
            if (edge.ToId == 0)
            {
                droppedUnresolved++;
                continue;
            }

            // Look up target type + realm + reverse label
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

        logger.LogInformation(
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
            // Skip edges that already have explicit temporal bounds (from Value parsing)
            if (edge.FromYear.HasValue)
                continue;

            var hasSrc = nodeLifecycleMap.TryGetValue(edge.FromId, out var src);
            var hasTgt = nodeLifecycleMap.TryGetValue(edge.ToId, out var tgt);

            if (hasSrc && hasTgt && src.start.HasValue && tgt.start.HasValue)
            {
                // Overlap: latest start to earliest end
                var from = Math.Max(src.start.Value, tgt.start.Value);
                var to = (src.end, tgt.end) switch
                {
                    (int a, int b) => Math.Min(a, b),
                    (int a, null) => a,
                    (null, int b) => b,
                    _ => (int?)null,
                };

                // Only set if the interval makes sense (start <= end)
                if (to is null || from <= to)
                {
                    edge.FromYear = from;
                    edge.ToYear = to;
                    derivedCount++;
                }
            }
            else if (hasSrc && src.start.HasValue && !hasTgt)
            {
                // Only source has lifecycle — edge starts with source
                edge.FromYear = src.start.Value;
                edge.ToYear = src.end;
                derivedCount++;
            }
            else if (hasTgt && tgt.start.HasValue && !hasSrc)
            {
                // Only target has lifecycle — edge starts with target
                edge.FromYear = tgt.start.Value;
                edge.ToYear = tgt.end;
                derivedCount++;
            }
        }

        logger.LogInformation(
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
            logger.LogInformation("InfoboxGraph: deduped {Dropped} duplicate edges ({Pre} → {Post})", deduped, preDedupe, filteredEdges.Count);

        // ── Hierarchy helpers: precompute transitive closures for tree/DAG-shaped labels.
        // Each registered lineage walks edges of a single label in a single direction from
        // every seed node and stores the ordered closure on the seed as `lineages.<key>`.
        // Cycle-safe: the BFS uses a visited set, so the two known apprentice_of cycles
        // (characters who trained each other) are handled transparently.
        // See ADR-003 Gap 1, Design-008, and HierarchyRegistry.
        ComputeLineageClosures(nodes, filteredEdges);

        // Write to MongoDB
        logger.LogInformation("InfoboxGraph: writing nodes...");
        if (nodes.Count > 0)
        {
            await _nodes.DeleteManyAsync(FilterDefinition<GraphNode>.Empty, ct);
            await _nodes.InsertManyAsync(nodes, new InsertManyOptions { IsOrdered = false }, ct);
        }

        logger.LogInformation("InfoboxGraph: writing edges...");
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
        // This is the single site that creates indexes on kg.edges. RelationshipGraphBuilderService
        // does NOT duplicate these — Phase 5 is a full delete+insert so we re-assert the index set
        // on every rebuild. Singleton prefixes (fromId, toId) are intentionally omitted: they are
        // covered by the compound (fromId, label) / (toId, label) indexes as leading-key prefixes.
        await _edges.Indexes.CreateManyAsync(
            [
                // Unique constraint + dedup key. Also covers queries leading with fromId
                // (e.g. outgoing-edge Find for a single entity) via the leading-key prefix.
                new CreateIndexModel<RelationshipEdge>(
                    Builders<RelationshipEdge>.IndexKeys.Ascending(e => e.FromId).Ascending(e => e.ToId).Ascending(e => e.Label),
                    new CreateIndexOptions { Name = "ix_fromId_toId_label", Unique = true }
                ),
                // Outgoing-with-label (per-hop BFS outgoing pass + forward-direction $graphLookup).
                new CreateIndexModel<RelationshipEdge>(Builders<RelationshipEdge>.IndexKeys.Ascending(e => e.FromId).Ascending(e => e.Label), new CreateIndexOptions { Name = "ix_fromId_label" }),
                // Inbound-with-label (per-hop BFS inbound pass + reverse-direction $graphLookup).
                new CreateIndexModel<RelationshipEdge>(Builders<RelationshipEdge>.IndexKeys.Ascending(e => e.ToId).Ascending(e => e.Label), new CreateIndexOptions { Name = "ix_toId_label" }),
                // Label-only scans (ListRelationshipLabels, analytics aggregations).
                new CreateIndexModel<RelationshipEdge>(Builders<RelationshipEdge>.IndexKeys.Ascending(e => e.Label), new CreateIndexOptions { Name = "ix_label" }),
                // Continuity-only scans (BrowseEdgeLabels etc.).
                new CreateIndexModel<RelationshipEdge>(Builders<RelationshipEdge>.IndexKeys.Ascending(e => e.Continuity), new CreateIndexOptions { Name = "ix_continuity" }),
                // LLM extraction path: edges written during Phase 6 are keyed by source article.
                new CreateIndexModel<RelationshipEdge>(Builders<RelationshipEdge>.IndexKeys.Ascending(e => e.SourcePageId), new CreateIndexOptions { Name = "ix_sourcePageId" }),
                // Forward/reverse edge pairs written by the LLM path (cleanup/dedup).
                new CreateIndexModel<RelationshipEdge>(Builders<RelationshipEdge>.IndexKeys.Ascending(e => e.PairId), new CreateIndexOptions { Name = "ix_pairId", Sparse = true }),
            ],
            ct
        );

        // Refresh the kg.labels registry from the freshly-written edges.
        await BuildLabelRegistryAsync(ct);

        // Refresh the kg.edges.bidir view — a bidirectional projection of kg.edges that exposes
        // each edge twice (forward + reverse with relabeled/flipped endpoints). Primary consumer
        // is any $graphLookup that needs mixed-direction traversal in a single pipeline, since
        // $graphLookup can only recurse in one direction per invocation.
        await EnsureBidirectionalEdgesViewAsync(ct);

        logger.LogInformation("InfoboxGraph: complete. {Nodes} nodes, {Edges} edges (from {RawEdges} raw), indexes created.", nodes.Count, filteredEdges.Count, edges.Count);
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

            // BFS from each seed. Closure is the full reachable set, ordered by hop distance
            // (near ancestors first, farthest last). A visited set handles cycles and
            // ensures each ancestor appears exactly once even if multiple paths reach it.
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

            logger.LogInformation(
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

        // Drop the existing view (if any) so we can replace its pipeline — MongoDB does not
        // support altering a view in place; create-or-replace = drop + createView.
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
            // Forward branch: annotate each stored edge with direction="forward".
            new BsonDocument("$addFields", new BsonDocument("direction", "forward")),
            // Reverse branch: union another scan of kg.edges, dropping edges without a reverse
            // label, then flip endpoints and swap the label.
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
                                    // Preserve original fields that don't flip.
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
        logger.LogInformation("InfoboxGraph: created view {ViewName} (forward + reverse branches)", ViewName);
    }

    /// <summary>
    /// Rebuild the <c>kg.labels</c> registry as a materialized view over <c>kg.edges</c>.
    /// Seeds each label's <c>reverse</c>/<c>description</c>/<c>fromTypes</c>/<c>toTypes</c> from
    /// <see cref="FieldSemantics.Relationships"/>, then overlays the observed usage count and
    /// the actually-observed from/to type sets via a single aggregation pipeline.
    /// </summary>
    public async Task BuildLabelRegistryAsync(CancellationToken ct = default)
    {
        logger.LogInformation("InfoboxGraph: rebuilding kg.labels registry...");

        // Definition-side seed: canonical label → (reverse, description).
        // Multiple FieldSemantics entries can map to the same label (e.g. "Affiliation" and
        // "Affiliations" both emit "affiliated_with") — dedupe by label.
        var seed = FieldSemantics.Relationships.Values.DistinctBy(d => d.Label, StringComparer.OrdinalIgnoreCase).ToDictionary(d => d.Label, d => d, StringComparer.OrdinalIgnoreCase);

        // Observed-side aggregation: group edges by label and collect usage stats + type cardinality.
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

        // Also include seed-only labels (known in FieldSemantics but not yet observed in edges)
        // so consumers can discover the full label vocabulary even on an empty KG.
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

        // MongoDB can't compound-index two parallel arrays; split into single-array indexes.
        await _labels.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<RelationshipLabel>(Builders<RelationshipLabel>.IndexKeys.Descending(l => l.UsageCount), new CreateIndexOptions { Name = "ix_usageCount" }),
                new CreateIndexModel<RelationshipLabel>(Builders<RelationshipLabel>.IndexKeys.Ascending(l => l.FromTypes), new CreateIndexOptions { Name = "ix_fromTypes" }),
                new CreateIndexModel<RelationshipLabel>(Builders<RelationshipLabel>.IndexKeys.Ascending(l => l.ToTypes), new CreateIndexOptions { Name = "ix_toTypes" }),
            ],
            ct
        );

        logger.LogInformation("InfoboxGraph: kg.labels registry has {Count} labels ({Observed} observed, {SeedOnly} seed-only)", labelDocs.Count, observed.Count, labelDocs.Count - observed.Count);
    }

    /// <summary>
    /// Build a lookup from wiki URL AND title → PageId so we can resolve link targets.
    /// Infobox links may use URLs that don't exactly match the page's stored wikiUrl
    /// (redirects, disambiguation, URL encoding differences), so we also match by title.
    /// </summary>
    async Task<Dictionary<string, int>> BuildWikiUrlLookupAsync(CancellationToken ct)
    {
        var lookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var cursor = await _pages.Find(FilterDefinition<Page>.Empty).Project(Builders<Page>.Projection.Include(p => p.PageId).Include(p => p.Title).Include(p => p.WikiUrl)).ToCursorAsync(ct);

        while (await cursor.MoveNextAsync(ct))
        {
            foreach (var doc in cursor.Current)
            {
                var pageId = doc[MongoFields.Id].AsInt32;
                var wikiUrl = doc.Contains(PageBsonFields.WikiUrl) ? doc[PageBsonFields.WikiUrl].AsString : null;
                var title = doc.Contains(PageBsonFields.Title) ? doc[PageBsonFields.Title].AsString : null;

                // Primary: exact URL match
                if (wikiUrl is not null)
                    lookup.TryAdd(wikiUrl, pageId);

                // Secondary: match by title (handles redirects and disambiguation)
                if (title is not null)
                    lookup.TryAdd(title, pageId);
            }
        }

        return lookup;
    }

    /// <summary>
    /// Resolve a link target to a PageId. Tries URL first, then link content text as title.
    /// </summary>
    int ResolveLinkTarget(string? href, string? content, Dictionary<string, int> lookup)
    {
        // Try exact URL match
        if (href is not null && lookup.TryGetValue(href, out var byUrl))
            return byUrl;

        // Try link display text as title
        if (content is not null && lookup.TryGetValue(content, out var byContent))
            return byContent;

        // Try extracting title from URL (decode /wiki/Title_Name)
        if (href is not null && href.Contains("/wiki/"))
        {
            var titleFromUrl = Uri.UnescapeDataString(href[(href.LastIndexOf("/wiki/") + 6)..]).Replace('_', ' ');
            if (lookup.TryGetValue(titleFromUrl, out var byExtracted))
                return byExtracted;
        }

        return 0; // unresolved
    }

    /// <summary>
    /// Match each Value string to its primary link from the link lookup.
    /// The primary entity is the text before the first '(' in the Value.
    /// Parenthetical text is treated as qualifier metadata, and temporal bounds are parsed from it.
    /// </summary>
    internal readonly record struct PrimaryLink(BsonDocument Link, string? Qualifier, int? FromYear, int? ToYear, string RawValue, int Order);

    static List<PrimaryLink> ExtractPrimaryLinks(List<string> values, Dictionary<string, BsonDocument> linkLookup)
    {
        var results = new List<PrimaryLink>();
        if (values.Count == 0 || linkLookup.Count == 0)
            return results;

        for (var i = 0; i < values.Count; i++)
        {
            var value = values[i];
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var rawValue = value.Trim();

            // Extract primary name: text before first '('
            var parenIdx = value.IndexOf('(');
            var primaryName = (parenIdx > 0 ? value[..parenIdx] : value).Trim().TrimEnd(',');

            // Extract qualifier: text inside parentheses
            string? qualifier = null;
            int? fromYear = null;
            int? toYear = null;

            if (parenIdx > 0)
            {
                var closeIdx = value.LastIndexOf(')');
                if (closeIdx > parenIdx)
                {
                    qualifier = value[(parenIdx + 1)..closeIdx].Trim();

                    // Parse temporal bounds from qualifier: "19 BBY–4 ABY", "5 ABY, officially"
                    var yearMatches = Regex.Matches(qualifier, @"(\d[\d,]*)\s*(BBY|ABY)", RegexOptions.IgnoreCase);
                    if (yearMatches.Count >= 1)
                        fromYear = ParseGalacticYear(yearMatches[0].Value);
                    if (yearMatches.Count >= 2)
                        toYear = ParseGalacticYear(yearMatches[1].Value);
                }
            }

            // Try an exact match first — fastest path for simple values like "Kamino".
            if (linkLookup.TryGetValue(primaryName, out var matchedLink))
            {
                results.Add(new PrimaryLink(matchedLink, qualifier, fromYear, toYear, rawValue, i));
            }
            else if (linkLookup.TryGetValue(rawValue, out var fullMatch))
            {
                // Fallback: try matching the full value text (handles values without parens)
                results.Add(new PrimaryLink(fullMatch, null, null, null, rawValue, i));
            }
            else
            {
                // Multi-entity match: many infobox values contain multiple linked entities
                // in the same string (e.g. "Prime Minister Lama Su", "Jedi General Anakin Skywalker").
                // Iterate ALL links and emit an edge for each link whose content appears inside the
                // primary name. Downstream filters (e.g. TitleOrPosition drop) will prune edges
                // whose target type doesn't match the relationship's expected target, so titles
                // like "Prime Minister" get dropped and the actual person "Lama Su" survives.
                var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (linkContent, linkDoc) in linkLookup)
                {
                    if (string.IsNullOrEmpty(linkContent))
                        continue;
                    if (primaryName.Contains(linkContent, StringComparison.OrdinalIgnoreCase) && emitted.Add(linkContent))
                    {
                        results.Add(new PrimaryLink(linkDoc, qualifier, fromYear, toYear, rawValue, i));
                    }
                }
            }
        }

        return results;
    }

    /// <summary>
    /// True if an edge label is expected to target a person/character.
    /// Derived from the registered <see cref="LabelDefinition.ExpectedTargetTypes"/>
    /// — no hardcoded list. Used to filter qualifier noise (e.g. apprentice_of → "Jedi Master"
    /// instead of the actual person).
    /// </summary>
    static bool IsPersonRelationshipLabel(string label) => InfoboxDefinitionRegistry.EdgeTargetsPerson(label);

    /// <summary>
    /// Parse a date text like "22 BBY", "c. 5100 BBY", "19 BBY, Felucia" into a sort-key year.
    /// Returns negative for BBY, positive for ABY, null if unparseable.
    /// </summary>
    static int? ParseGalacticYear(string text)
    {
        var match = Regex.Match(text, @"(\d[\d,]*)\s*(BBY|ABY)", RegexOptions.IgnoreCase);
        if (!match.Success)
            return null;
        if (!int.TryParse(match.Groups[1].Value.Replace(",", ""), out var num))
            return null;
        return match.Groups[2].Value.Equals("BBY", StringComparison.OrdinalIgnoreCase) ? -num : num;
    }

    /// <summary>
    /// Extract every BBY/ABY year occurrence from a range-style value like
    /// <c>"25,000 BBY – 19 BBY"</c> or <c>"1,032 BBY – 0 BBY / 0 ABY"</c>.
    /// Returns the parsed sort-key years sorted chronologically (most-negative first)
    /// so callers can label them as start/end. Duplicates are deduped.
    /// </summary>
    static List<int> ParseAllGalacticYears(string text)
    {
        var matches = Regex.Matches(text, @"(\d[\d,]*)\s*(BBY|ABY)", RegexOptions.IgnoreCase);
        var years = new SortedSet<int>();
        foreach (Match m in matches)
        {
            if (!int.TryParse(m.Groups[1].Value.Replace(",", ""), out var num))
                continue;
            var isBby = m.Groups[2].Value.Equals("BBY", StringComparison.OrdinalIgnoreCase);
            years.Add(isBby ? -num : num);
        }
        return [.. years];
    }

    /// <summary>
    /// Parse a real-world date text like "October 23, 1959", "September 2017", "2015" into a CE year.
    /// </summary>
    static int? ParseRealWorldYear(string text)
    {
        // "October 23, 1959" or "November 19, 1983 in" — extract 4-digit year
        var match = Regex.Match(text, @"\b(1[89]\d{2}|20[0-3]\d)\b");
        return match.Success && int.TryParse(match.Value, out var year) ? year : null;
    }

    /// <summary>
    /// Detect calendar system and parse year from date text.
    /// Tries galactic (BBY/ABY) first, then real-world, then unknown.
    /// </summary>
    static (string calendar, int? year) DetectCalendarAndParse(string text, string calendarHint)
    {
        if (calendarHint == "galactic")
        {
            var gy = ParseGalacticYear(text);
            return ("galactic", gy); // null year is fine — vague galactic date
        }

        if (calendarHint == "real")
        {
            var ry = ParseRealWorldYear(text);
            return ("real", ry);
        }

        // "auto" — try galactic first (unambiguous), then real-world
        var galactic = ParseGalacticYear(text);
        if (galactic.HasValue)
            return ("galactic", galactic);

        var real = ParseRealWorldYear(text);
        if (real.HasValue)
            return ("real", real);

        return ("unknown", null);
    }

    /// <summary>
    /// Assign Order values to facets within each semantic dimension prefix.
    /// Groups by dimension (e.g. "institutional"), sorts by year (nulls last), assigns 0-based order.
    /// </summary>
    static void AssignFacetOrder(List<TemporalFacet> facets)
    {
        var groups = facets.GroupBy(f => f.Semantic.Split('.')[0]);
        foreach (var group in groups)
        {
            var ordered = group
                .OrderBy(f => f.Year.HasValue ? 0 : 1) // year-bearing first
                .ThenBy(f => f.Year ?? int.MaxValue)
                .ToList();
            for (var i = 0; i < ordered.Count; i++)
                ordered[i].Order = i;
        }
    }

    /// <summary>
    /// Convert an infobox field label into a snake_case edge label.
    /// </summary>
    static string NormaliseLabel(string label)
    {
        // Remove parenthetical suffixes like "(s)" and clean up
        var clean = label.Replace("(s)", "").Replace("(", "").Replace(")", "").Trim();

        // Convert to snake_case
        var result = new System.Text.StringBuilder();
        for (var i = 0; i < clean.Length; i++)
        {
            var c = clean[i];
            if (c == ' ' || c == '-' || c == '/')
            {
                result.Append('_');
            }
            else if (char.IsUpper(c) && i > 0 && !char.IsUpper(clean[i - 1]))
            {
                result.Append('_');
                result.Append(char.ToLowerInvariant(c));
            }
            else
            {
                result.Append(char.ToLowerInvariant(c));
            }
        }

        return result.ToString().Trim('_');
    }
}
