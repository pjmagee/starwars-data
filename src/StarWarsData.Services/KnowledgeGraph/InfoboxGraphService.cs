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
                    .Include(p => p.Universe)
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
                var universe = doc.Contains(PageBsonFields.Universe)
                    ? Enum.TryParse<Universe>(doc[PageBsonFields.Universe].AsString, out var u)
                        ? u
                        : Universe.Unknown
                    : Universe.Unknown;
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
                            foreach (var (linkDoc, qualifier, fromYear, toYear) in primaryLinks)
                            {
                                var href = linkDoc.Contains(InfoboxBsonFields.Href) ? linkDoc[InfoboxBsonFields.Href].AsString : null;
                                var content = linkDoc.Contains(InfoboxBsonFields.Content) ? linkDoc[InfoboxBsonFields.Content].AsString : null;
                                if (href is null || content is null)
                                    continue;

                                var targetPageId = ResolveLinkTarget(href, content, wikiUrlToPageId);

                                edges.Add(
                                    new RelationshipEdge
                                    {
                                        FromId = pageId,
                                        FromName = title,
                                        FromType = type,
                                        ToId = targetPageId,
                                        ToName = content,
                                        ToType = "",
                                        Label = edgeLabel,
                                        Weight = weight,
                                        Evidence = qualifier is not null ? $"Infobox field '{label}': {content} ({qualifier})" : $"Infobox field '{label}'",
                                        SourcePageId = pageId,
                                        Continuity = continuity,
                                        FromYear = fromYear,
                                        ToYear = toYear,
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
                        Universe = universe,
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

        // ── Post-processing: filter noise edges and enrich with target type ──
        var nodeTypeMap = nodes.ToDictionary(n => n.PageId, n => n.Type);

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

            // Look up target type
            var targetType = nodeTypeMap.GetValueOrDefault(edge.ToId, "");
            edge.ToType = targetType;

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
            ],
            ct
        );

        await _edges.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<RelationshipEdge>(Builders<RelationshipEdge>.IndexKeys.Ascending(e => e.FromId)),
                new CreateIndexModel<RelationshipEdge>(Builders<RelationshipEdge>.IndexKeys.Ascending(e => e.ToId)),
                new CreateIndexModel<RelationshipEdge>(Builders<RelationshipEdge>.IndexKeys.Ascending(e => e.Label)),
                new CreateIndexModel<RelationshipEdge>(Builders<RelationshipEdge>.IndexKeys.Ascending(e => e.Continuity)),
                new CreateIndexModel<RelationshipEdge>(Builders<RelationshipEdge>.IndexKeys.Ascending(e => e.FromId).Ascending(e => e.Label)),
            ],
            ct
        );

        logger.LogInformation("InfoboxGraph: complete. {Nodes} nodes, {Edges} edges (from {RawEdges} raw), indexes created.", nodes.Count, filteredEdges.Count, edges.Count);
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
    static List<(BsonDocument link, string? qualifier, int? fromYear, int? toYear)> ExtractPrimaryLinks(List<string> values, Dictionary<string, BsonDocument> linkLookup)
    {
        var results = new List<(BsonDocument link, string? qualifier, int? fromYear, int? toYear)>();
        if (values.Count == 0 || linkLookup.Count == 0)
            return results;

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

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
                    {
                        var y1 = ParseGalacticYear(yearMatches[0].Value);
                        fromYear = y1;
                    }
                    if (yearMatches.Count >= 2)
                    {
                        var y2 = ParseGalacticYear(yearMatches[1].Value);
                        toYear = y2;
                    }
                }
            }

            // Try an exact match first — fastest path for simple values like "Kamino".
            if (linkLookup.TryGetValue(primaryName, out var matchedLink))
            {
                results.Add((matchedLink, qualifier, fromYear, toYear));
            }
            else if (linkLookup.TryGetValue(value.Trim(), out var fullMatch))
            {
                // Fallback: try matching the full value text (handles values without parens)
                results.Add((fullMatch, null, null, null));
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
                        results.Add((linkDoc, qualifier, fromYear, toYear));
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
