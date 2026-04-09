using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries;
using StarWarsData.Services.KnowledgeGraph.Definitions;

namespace StarWarsData.Services;

/// <summary>
/// Read-only query service for the knowledge graph (kg.nodes + kg.edges).
/// Powers the Graph Explorer page, Knowledge Graph page, and render_graph tool.
/// </summary>
public class KnowledgeGraphQueryService(IMongoClient mongoClient, IOptions<SettingsOptions> settings)
{
    readonly IMongoCollection<GraphNode> _nodes = mongoClient.GetDatabase(settings.Value.DatabaseName).GetCollection<GraphNode>(Collections.KgNodes);

    readonly IMongoCollection<RelationshipEdge> _edges = mongoClient.GetDatabase(settings.Value.DatabaseName).GetCollection<RelationshipEdge>(Collections.KgEdges);

    public async Task<List<string>> GetEntityTypesAsync(CancellationToken ct)
    {
        var types = await _nodes.DistinctAsync(new StringFieldDefinition<GraphNode, string>(GraphNodeBsonFields.Type), FilterDefinition<GraphNode>.Empty, cancellationToken: ct);
        return (await types.ToListAsync(ct)).Where(t => !string.IsNullOrEmpty(t)).OrderBy(t => t).ToList();
    }

    public async Task<List<string>> GetEdgeLabelsAsync(CancellationToken ct)
    {
        var labels = await _edges.DistinctAsync(new StringFieldDefinition<RelationshipEdge, string>(RelationshipEdgeBsonFields.Label), FilterDefinition<RelationshipEdge>.Empty, cancellationToken: ct);
        return (await labels.ToListAsync(ct)).Where(l => !string.IsNullOrEmpty(l)).OrderBy(l => l).ToList();
    }

    public async Task<BrowseEntitiesResult> BrowseAsync(string? type, string? q, int page, int pageSize, string? continuity, string? realm, CancellationToken ct)
    {
        if (pageSize > 100)
            pageSize = 100;

        var filters = new List<FilterDefinition<GraphNode>>();

        if (!string.IsNullOrWhiteSpace(type))
            filters.Add(Builders<GraphNode>.Filter.Eq(n => n.Type, type));
        if (!string.IsNullOrWhiteSpace(q))
            filters.Add(Builders<GraphNode>.Filter.Regex(n => n.Name, new BsonRegularExpression(q, "i")));
        if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont))
            filters.Add(Builders<GraphNode>.Filter.Eq(n => n.Continuity, cont));
        if (realm is not null && Enum.TryParse<Realm>(realm, true, out var r))
            filters.Add(Builders<GraphNode>.Filter.In(n => n.Realm, [r, Realm.Unknown]));

        var filter = filters.Count > 0 ? Builders<GraphNode>.Filter.And(filters) : FilterDefinition<GraphNode>.Empty;

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

    public async Task<List<EntitySearchDto>> SearchAsync(string q, string? type, string? continuity, string? realm, CancellationToken ct)
    {
        var filters = new List<FilterDefinition<GraphNode>> { Builders<GraphNode>.Filter.Regex(n => n.Name, new BsonRegularExpression(q, "i")) };

        if (!string.IsNullOrWhiteSpace(type))
            filters.Add(Builders<GraphNode>.Filter.Eq(n => n.Type, type));
        if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont))
            filters.Add(Builders<GraphNode>.Filter.Eq(n => n.Continuity, cont));
        if (realm is not null && Enum.TryParse<Realm>(realm, true, out var r))
            filters.Add(Builders<GraphNode>.Filter.In(n => n.Realm, [r, Realm.Unknown]));

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

    /// <summary>
    /// Returns distinct relationship labels for an entity plus the node's type
    /// and a pre-computed "default enabled" subset for the UI. Includes both:
    /// <list type="bullet">
    ///   <item>Outgoing edges: the label as stored (entity is the source).</item>
    ///   <item>Incoming edges: the edge label mapped to its reverse from
    ///     <see cref="FieldSemantics"/>, so they read naturally from the entity's
    ///     perspective. For example, a Battle → commanded_by → Character edge
    ///     surfaces on the Character as <c>commanded</c>.</item>
    /// </list>
    /// Inbound edges whose label has no registered reverse in <see cref="FieldSemantics"/>
    /// are intentionally dropped — without a known reverse we cannot present them from
    /// this node's perspective coherently, and they remain visible on the source node's
    /// outgoing edges.
    /// </summary>
    public async Task<EntityLabelsResult> GetLabelsForEntityAsync(int pageId, CancellationToken ct)
    {
        var edgesRaw = _edges.Database.GetCollection<BsonDocument>(Collections.KgEdges);

        // Fetch the node type in parallel with the edge aggregations — we need it
        // to compute default-enabled labels via DefaultLabelSelector.
        var typeTask = _nodes.Find(n => n.PageId == pageId).Project(n => n.Type).FirstOrDefaultAsync(ct);

        // Distinct outgoing labels (entity as source)
        var outgoingTask = edgesRaw
            .Aggregate<BsonDocument>(
                new[]
                {
                    new BsonDocument("$match", new BsonDocument(RelationshipEdgeBsonFields.FromId, pageId)),
                    new BsonDocument("$group", new BsonDocument(MongoFields.Id, "$" + RelationshipEdgeBsonFields.Label)),
                }
            )
            .ToListAsync(ct);

        // Distinct incoming labels (entity as target)
        var incomingTask = edgesRaw
            .Aggregate<BsonDocument>(
                new[]
                {
                    new BsonDocument("$match", new BsonDocument(RelationshipEdgeBsonFields.ToId, pageId)),
                    new BsonDocument("$group", new BsonDocument(MongoFields.Id, "$" + RelationshipEdgeBsonFields.Label)),
                }
            )
            .ToListAsync(ct);

        await Task.WhenAll(typeTask, outgoingTask, incomingTask);
        var entityType = typeTask.Result ?? string.Empty;
        var outgoing = outgoingTask.Result;
        var incoming = incomingTask.Result;

        var labelSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var doc in outgoing)
            labelSet.Add(doc[MongoFields.Id].AsString);

        // Map inbound edge labels to their reverse form so they read from this node's
        // perspective. FieldSemantics holds canonical reverses (commanded_by → commanded,
        // apprentice_of → master_of, etc.). Inbound edges without a registered reverse
        // are dropped to avoid presenting ambiguous/backwards relationships.
        var reverseLookup = FieldSemantics.Relationships.Values.DistinctBy(d => d.Label).ToDictionary(d => d.Label, d => d.Reverse, StringComparer.OrdinalIgnoreCase);

        foreach (var doc in incoming)
        {
            var inbound = doc[MongoFields.Id].AsString;
            if (reverseLookup.TryGetValue(inbound, out var reverse) && !string.IsNullOrEmpty(reverse))
                labelSet.Add(reverse);
            // else: no registered reverse → skip; edge is still visible on the source node.
        }

        var labels = labelSet.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        var defaults = DefaultLabelSelector.GetDefaults(entityType, labels);

        return new EntityLabelsResult
        {
            Type = entityType,
            Labels = labels,
            DefaultEnabled = defaults,
        };
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
        string? calendar,
        string? sortBy,
        string? sortDirection,
        string? realm,
        CancellationToken ct
    )
    {
        if (pageSize > 100)
            pageSize = 100;

        HashSet<int>? nodeIdsWithLabel = null;
        if (!string.IsNullOrWhiteSpace(label))
        {
            var edgeFilter = Builders<RelationshipEdge>.Filter.Eq(e => e.Label, label);
            var matchingFromIds = await _edges.Distinct(new StringFieldDefinition<RelationshipEdge, int>(RelationshipEdgeBsonFields.FromId), edgeFilter, cancellationToken: ct).ToListAsync(ct);
            nodeIdsWithLabel = [.. matchingFromIds];
        }

        var filters = new List<FilterDefinition<GraphNode>>();

        if (nodeIdsWithLabel is not null)
            filters.Add(Builders<GraphNode>.Filter.In(n => n.PageId, nodeIdsWithLabel));
        if (!string.IsNullOrWhiteSpace(type))
            filters.Add(Builders<GraphNode>.Filter.Eq(n => n.Type, type));
        if (!string.IsNullOrWhiteSpace(q))
            filters.Add(Builders<GraphNode>.Filter.Regex(n => n.Name, new BsonRegularExpression(q, "i")));
        if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont))
            filters.Add(Builders<GraphNode>.Filter.Eq(n => n.Continuity, cont));
        if (realm is not null && Enum.TryParse<Realm>(realm, true, out var r))
            filters.Add(Builders<GraphNode>.Filter.In(n => n.Realm, [r, Realm.Unknown]));
        if (temporalOnly)
            filters.Add(Builders<GraphNode>.Filter.SizeGt(n => n.TemporalFacets, 0));
        // Calendar-aware temporal filter: if a calendar (galactic/real) is specified,
        // scope the year range to facets of that calendar (via $elemMatch). Otherwise
        // fall back to the flat startYear/endYear envelope.
        var hasCalendar = !string.IsNullOrWhiteSpace(calendar);
        if (hasCalendar || !string.IsNullOrWhiteSpace(semantic))
        {
            var facetClauses = new List<FilterDefinition<TemporalFacet>>();
            if (!string.IsNullOrWhiteSpace(semantic))
                facetClauses.Add(Builders<TemporalFacet>.Filter.Regex(f => f.Semantic, new BsonRegularExpression($"^{Regex.Escape(semantic)}", "i")));
            if (hasCalendar)
                facetClauses.Add(Builders<TemporalFacet>.Filter.Eq(f => f.Calendar, calendar));
            if (yearFrom.HasValue)
                facetClauses.Add(Builders<TemporalFacet>.Filter.Gte(f => f.Year, yearFrom.Value));
            if (yearTo.HasValue)
                facetClauses.Add(Builders<TemporalFacet>.Filter.Lte(f => f.Year, yearTo.Value));

            if (facetClauses.Count > 0)
                filters.Add(Builders<GraphNode>.Filter.ElemMatch(n => n.TemporalFacets, Builders<TemporalFacet>.Filter.And(facetClauses)));
        }
        else
        {
            // No calendar/semantic filter — use the envelope for year range.
            if (yearFrom.HasValue)
                filters.Add(Builders<GraphNode>.Filter.Or(Builders<GraphNode>.Filter.Gte(n => n.EndYear, yearFrom.Value), Builders<GraphNode>.Filter.Eq(n => n.EndYear, null)));
            if (yearTo.HasValue)
                filters.Add(Builders<GraphNode>.Filter.Lte(n => n.StartYear, yearTo.Value));
        }

        var filter = filters.Count > 0 ? Builders<GraphNode>.Filter.And(filters) : FilterDefinition<GraphNode>.Empty;

        var total = await _nodes.CountDocumentsAsync(filter, cancellationToken: ct);

        // Apply sort — map client column names to MongoDB fields.
        // "ascending" | "descending" (case-insensitive); anything else = ascending.
        var ascending = !string.Equals(sortDirection, "descending", StringComparison.OrdinalIgnoreCase);
        var sortField = (sortBy ?? "name").ToLowerInvariant() switch
        {
            GraphNodeBsonFields.Type => GraphNodeBsonFields.Type,
            GraphNodeBsonFields.Continuity => GraphNodeBsonFields.Continuity,
            "startyear" or "start_year" => GraphNodeBsonFields.StartYear,
            "endyear" or "end_year" => GraphNodeBsonFields.EndYear,
            _ => GraphNodeBsonFields.Name, // default
        };
        var sort = ascending ? Builders<GraphNode>.Sort.Ascending(sortField) : Builders<GraphNode>.Sort.Descending(sortField);

        var items = await _nodes.Find(filter).Sort(sort).Skip((page - 1) * pageSize).Limit(pageSize).ToListAsync(ct);

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
        bool onlyRoot = false,
        string? realm = null,
        int? yearFrom = null,
        int? yearTo = null,
        int maxNodes = 200,
        CancellationToken ct = default
    )
    {
        maxDepth = Math.Clamp(maxDepth, 1, 4);
        maxNodes = Math.Clamp(maxNodes, 10, 1000);
        var edgesRaw = _edges.Database.GetCollection<BsonDocument>(Collections.KgEdges);

        // Optional temporal window filter: keep only edges whose [fromYear, toYear] interval
        // overlaps [yearFrom, yearTo]. Null edge bounds are treated as open (±∞) and pass —
        // this is recall-biased because most edges in kg.edges don't carry temporal bounds yet.
        // Overlap = (edge.fromYear is null OR edge.fromYear <= yearTo)
        //        AND (edge.toYear   is null OR edge.toYear   >= yearFrom)
        FilterDefinition<BsonDocument>? temporalFilter = null;
        if (yearFrom.HasValue || yearTo.HasValue)
        {
            var b = Builders<BsonDocument>.Filter;
            var clauses = new List<FilterDefinition<BsonDocument>>();
            if (yearTo.HasValue)
            {
                clauses.Add(
                    b.Or(b.Eq(RelationshipEdgeBsonFields.FromYear, BsonNull.Value), b.Not(b.Exists(RelationshipEdgeBsonFields.FromYear)), b.Lte(RelationshipEdgeBsonFields.FromYear, yearTo.Value))
                );
            }
            if (yearFrom.HasValue)
            {
                clauses.Add(
                    b.Or(b.Eq(RelationshipEdgeBsonFields.ToYear, BsonNull.Value), b.Not(b.Exists(RelationshipEdgeBsonFields.ToYear)), b.Gte(RelationshipEdgeBsonFields.ToYear, yearFrom.Value))
                );
            }
            temporalFilter = b.And(clauses);
        }

        // Realm is denormalized onto edges (fromRealm/toRealm) during Phase 5 ETL, so we
        // push it directly into the edge filter instead of the per-edge node lookups the
        // old implementation used. Unknown endpoints always pass the filter, preserving
        // the prior "dangling edges are kept" semantics.
        Realm? realmFilter = null;
        if (!string.IsNullOrWhiteSpace(realm) && Enum.TryParse<Realm>(realm, true, out var rParsed))
            realmFilter = rParsed;

        FilterDefinition<BsonDocument>? outRealmFilter = null;
        FilterDefinition<BsonDocument>? inRealmFilter = null;
        if (realmFilter is not null)
        {
            var b = Builders<BsonDocument>.Filter;
            var acceptedRealms = new[] { realmFilter.Value.ToString(), Realm.Unknown.ToString() };
            // Back-compat: edges written before the realm denormalization landed won't
            // carry the field. Treat "field missing" the same as Unknown so filtered
            // queries don't silently lose all results until Phase 5 ETL re-runs.
            outRealmFilter = b.Or(b.Not(b.Exists(RelationshipEdgeBsonFields.ToRealm)), b.In(RelationshipEdgeBsonFields.ToRealm, acceptedRealms));
            inRealmFilter = b.Or(b.Not(b.Exists(RelationshipEdgeBsonFields.FromRealm)), b.In(RelationshipEdgeBsonFields.FromRealm, acceptedRealms));
        }

        // Fast-fail: if the root node itself doesn't match the realm filter, return empty.
        if (realmFilter is not null)
        {
            var rootRealm = await _nodes.Find(n => n.PageId == pageId).Project(n => (Realm?)n.Realm).FirstOrDefaultAsync(ct) ?? Realm.Unknown;
            if (rootRealm != realmFilter.Value && rootRealm != Realm.Unknown)
            {
                return new RelationshipGraphResult
                {
                    RootId = pageId,
                    RootName = $"#{pageId}",
                    Nodes = [],
                    Edges = [],
                };
            }
        }

        // Early exit: client explicitly asked for just the root node and no edges.
        // This is set by the frontend when the user toggles all labels off via the
        // None button — they want to focus on a single entity without its relationships.
        // Using a dedicated flag avoids ASP.NET Core's empty-string-to-null coercion
        // that would otherwise make `labels=""` indistinguishable from no filter.
        if (onlyRoot)
        {
            var rootOnly = await _nodes.Find(n => n.PageId == pageId).FirstOrDefaultAsync(ct);
            if (rootOnly is null)
            {
                return new RelationshipGraphResult
                {
                    RootId = pageId,
                    RootName = $"#{pageId}",
                    Nodes = [],
                    Edges = [],
                };
            }
            return new RelationshipGraphResult
            {
                RootId = pageId,
                RootName = rootOnly.Name,
                Nodes =
                [
                    new RelationshipGraphNode
                    {
                        Id = rootOnly.PageId,
                        Name = rootOnly.Name,
                        Type = rootOnly.Type,
                        ImageUrl = rootOnly.ImageUrl ?? string.Empty,
                    },
                ],
                Edges = [],
            };
        }

        // Label filter resolution:
        // The client may pass labels that are either forward labels (commanded_by) or
        // reverse labels that only exist from the target node's perspective (commanded).
        // For the outgoing edge query we need forward labels as stored in the DB.
        // For the inbound edge query we also need forward labels — but we include the
        // forward form of any reverse label the client requested, so "Anakin commanded X"
        // pulls in edges stored as "X commanded_by Anakin".
        string[]? requestedLabels = null;
        HashSet<string>? outgoingLabelFilter = null;
        HashSet<string>? inboundLabelFilter = null;
        // Map of forward-label → reverse-label, used to rewrite inbound edges so they
        // render from the perspective of the currently-selected node.
        var forwardToReverse = FieldSemantics.Relationships.Values.DistinctBy(d => d.Label).ToDictionary(d => d.Label, d => d.Reverse, StringComparer.OrdinalIgnoreCase);
        // Reverse lookup: reverse-label → forward-label, so if the client asks for
        // "commanded" we query the DB for "commanded_by".
        var reverseToForward = FieldSemantics
            .Relationships.Values.DistinctBy(d => d.Reverse)
            .Where(d => !string.IsNullOrEmpty(d.Reverse))
            .ToDictionary(d => d.Reverse, d => d.Label, StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(labels))
        {
            requestedLabels = labels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            outgoingLabelFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            inboundLabelFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in requestedLabels)
            {
                // Outgoing: the label is stored as-is on the edge.
                outgoingLabelFilter.Add(l);
                // Inbound: look up the forward form. If the requested label is already a
                // forward label, it will also map (e.g. commanded_by stays as commanded_by).
                if (reverseToForward.TryGetValue(l, out var fwd))
                    inboundLabelFilter.Add(fwd);
                else
                    inboundLabelFilter.Add(l);
            }
        }

        var visited = new HashSet<int> { pageId };
        var allEdges = new List<(int from, string fromName, int to, string toName, string label, double weight, int? fromYear, int? toYear)>();
        var frontier = new HashSet<int> { pageId };

        var truncated = false;
        for (var depth = 0; depth < maxDepth && frontier.Count > 0 && !truncated; depth++)
        {
            // ── Outgoing edges (frontier node is the source) ──
            var outFilter = Builders<BsonDocument>.Filter.In(RelationshipEdgeBsonFields.FromId, frontier);
            if (outgoingLabelFilter is not null)
                outFilter &= Builders<BsonDocument>.Filter.In(RelationshipEdgeBsonFields.Label, outgoingLabelFilter);
            if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont))
                outFilter &= Builders<BsonDocument>.Filter.Eq(GraphNodeBsonFields.Continuity, cont.ToString());
            if (temporalFilter is not null)
                outFilter &= temporalFilter;
            if (outRealmFilter is not null)
                outFilter &= outRealmFilter;

            var outEdges = await edgesRaw.Find(outFilter).Limit(200).ToListAsync(ct);

            // ── Inbound edges (frontier node is the target) ──
            // Rewritten at read time: we flip from/to and map the label to its reverse,
            // so the resulting edge reads naturally from the frontier node's perspective.
            var inFilter = Builders<BsonDocument>.Filter.In(RelationshipEdgeBsonFields.ToId, frontier);
            if (inboundLabelFilter is not null)
                inFilter &= Builders<BsonDocument>.Filter.In(RelationshipEdgeBsonFields.Label, inboundLabelFilter);
            if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont2))
                inFilter &= Builders<BsonDocument>.Filter.Eq(GraphNodeBsonFields.Continuity, cont2.ToString());
            if (temporalFilter is not null)
                inFilter &= temporalFilter;
            if (inRealmFilter is not null)
                inFilter &= inRealmFilter;

            var inEdges = await edgesRaw.Find(inFilter).Limit(200).ToListAsync(ct);

            var nextFrontier = new HashSet<int>();

            foreach (var e in outEdges)
            {
                var toId = e[RelationshipEdgeBsonFields.ToId].AsInt32;
                var fromId = e[RelationshipEdgeBsonFields.FromId].AsInt32;
                // Realm filter is already applied server-side via outRealmFilter.
                allEdges.Add(
                    (
                        fromId,
                        e[RelationshipEdgeBsonFields.FromName].AsString,
                        toId,
                        e[RelationshipEdgeBsonFields.ToName].AsString,
                        e[RelationshipEdgeBsonFields.Label].AsString,
                        e.Contains(RelationshipEdgeBsonFields.Weight) ? e[RelationshipEdgeBsonFields.Weight].ToDouble() : 0.8,
                        e.Contains(RelationshipEdgeBsonFields.FromYear) && !e[RelationshipEdgeBsonFields.FromYear].IsBsonNull ? e[RelationshipEdgeBsonFields.FromYear].AsInt32 : null,
                        e.Contains(RelationshipEdgeBsonFields.ToYear) && !e[RelationshipEdgeBsonFields.ToYear].IsBsonNull ? e[RelationshipEdgeBsonFields.ToYear].AsInt32 : null
                    )
                );

                if (visited.Count < maxNodes && visited.Add(toId))
                    nextFrontier.Add(toId);
                else if (visited.Count >= maxNodes)
                    truncated = true;
            }

            foreach (var e in inEdges)
            {
                var origFromId = e[RelationshipEdgeBsonFields.FromId].AsInt32;
                var origToId = e[RelationshipEdgeBsonFields.ToId].AsInt32;
                var origLabel = e[RelationshipEdgeBsonFields.Label].AsString;

                // Skip if we already captured this edge in the outgoing pass
                // (possible when both endpoints are in the frontier).
                if (frontier.Contains(origFromId))
                    continue;
                // Realm filter is already applied server-side via inRealmFilter.

                // Flip the edge so the frontier node is the source, and rewrite
                // the label to its reverse form so the graph reads correctly.
                var displayLabel = forwardToReverse.TryGetValue(origLabel, out var rev) && !string.IsNullOrEmpty(rev) ? rev : origLabel;

                allEdges.Add(
                    (
                        origToId, // flipped: was toId, now source
                        e[RelationshipEdgeBsonFields.ToName].AsString,
                        origFromId, // flipped: was fromId, now target
                        e[RelationshipEdgeBsonFields.FromName].AsString,
                        displayLabel,
                        e.Contains(RelationshipEdgeBsonFields.Weight) ? e[RelationshipEdgeBsonFields.Weight].ToDouble() : 0.8,
                        e.Contains(RelationshipEdgeBsonFields.FromYear) && !e[RelationshipEdgeBsonFields.FromYear].IsBsonNull ? e[RelationshipEdgeBsonFields.FromYear].AsInt32 : null,
                        e.Contains(RelationshipEdgeBsonFields.ToYear) && !e[RelationshipEdgeBsonFields.ToYear].IsBsonNull ? e[RelationshipEdgeBsonFields.ToYear].AsInt32 : null
                    )
                );

                if (visited.Count < maxNodes && visited.Add(origFromId))
                    nextFrontier.Add(origFromId);
                else if (visited.Count >= maxNodes)
                    truncated = true;
            }

            frontier = nextFrontier;
        }

        // Filter edges to only include those where both endpoints are in the visited set
        var filteredEdges = allEdges.Where(e => visited.Contains(e.from) && visited.Contains(e.to)).DistinctBy(e => (e.from, e.to, e.label)).ToList();

        var rootNode = await _nodes.Find(n => n.PageId == pageId).FirstOrDefaultAsync(ct);
        var nodeIds = visited.ToList();
        var nodeDocs = await _nodes.Find(Builders<GraphNode>.Filter.In(n => n.PageId, nodeIds)).ToListAsync(ct);
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
                        ImageUrl = node.ImageUrl ?? string.Empty,
                    }
                );
            }
            else
            {
                var edge = filteredEdges.FirstOrDefault(e => e.to == id || e.from == id);
                resultNodes.Add(
                    new RelationshipGraphNode
                    {
                        Id = id,
                        Name = edge.to == id ? edge.toName : edge.fromName,
                        Type = "",
                    }
                );
            }
        }

        // Deduplicate edges between the same directed node pair.
        // Multiple edges A→B with different labels (e.g. "includes_battle" + "fought_in")
        // are merged into one edge with the labels joined. This avoids overlapping lines
        // and duplicate arrows in the D3 graph.
        var resultEdges = filteredEdges
            .GroupBy(e => (e.from, e.to))
            .Select(g =>
            {
                var best = g.OrderByDescending(e => e.weight).First();
                var labels = g.Select(e => e.label).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return new RelationshipGraphEdge
                {
                    FromId = best.from,
                    ToId = best.to,
                    Label = string.Join(" / ", labels),
                    Weight = best.weight,
                    FromYear = best.fromYear,
                    ToYear = best.toYear,
                };
            })
            .ToList();

        return new RelationshipGraphResult
        {
            RootId = pageId,
            RootName = rootNode?.Name ?? $"#{pageId}",
            Nodes = resultNodes,
            Edges = resultEdges,
            Truncated = truncated,
        };
    }

    // ── Core query methods shared by GraphRAGToolkit and controller ──

    /// <summary>Search nodes by name with optional type/continuity filter.</summary>
    public async Task<List<GraphNode>> SearchNodesAsync(string query, string? type, string? continuity, int limit, CancellationToken ct = default)
    {
        var filters = new List<FilterDefinition<GraphNode>> { Builders<GraphNode>.Filter.Regex(n => n.Name, new BsonRegularExpression(query, "i")) };
        if (!string.IsNullOrWhiteSpace(type))
            filters.Add(Builders<GraphNode>.Filter.Eq(n => n.Type, type));
        if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont))
            filters.Add(Builders<GraphNode>.Filter.Eq(n => n.Continuity, cont));

        return await _nodes.Find(Builders<GraphNode>.Filter.And(filters)).SortBy(n => n.Name).Limit(Math.Min(limit, 50)).ToListAsync(ct);
    }

    /// <summary>Get a single node by PageId.</summary>
    public Task<GraphNode?> GetNodeByIdAsync(int pageId, CancellationToken ct = default) => _nodes.Find(n => n.PageId == pageId).FirstOrDefaultAsync(ct)!;

    /// <summary>
    /// Batch-load a summary of properties for multiple nodes by PageId.
    /// Returns a dictionary of PageId → top properties (first value per key, up to 8 keys).
    /// Used to enrich relationship results so the agent doesn't need N follow-up calls.
    /// </summary>
    public async Task<Dictionary<int, Dictionary<string, string>>> GetNodePropertiesBatchAsync(List<int> pageIds, CancellationToken ct = default)
    {
        if (pageIds.Count == 0)
            return [];

        var filter = Builders<GraphNode>.Filter.In(n => n.PageId, pageIds);
        var nodes = await _nodes.Find(filter).Project(n => new { n.PageId, n.Properties }).ToListAsync(ct);

        var result = new Dictionary<int, Dictionary<string, string>>();
        foreach (var node in nodes)
        {
            if (node.Properties is null || node.Properties.Count == 0)
                continue;

            var summary = new Dictionary<string, string>();
            foreach (var kvp in node.Properties.Take(8))
            {
                var val = kvp.Value?.FirstOrDefault();
                if (!string.IsNullOrEmpty(val))
                    summary[kvp.Key] = val.Length > 100 ? val[..100] + "…" : val;
            }
            if (summary.Count > 0)
                result[node.PageId] = summary;
        }

        return result;
    }

    /// <summary>Find nodes by temporal range with optional semantic dimension filter.</summary>
    public async Task<List<GraphNode>> FindNodesByYearAsync(int year, string type, int? yearEnd, string? continuity, string? semantic, int limit, CancellationToken ct = default)
    {
        var rangeStart = Math.Min(year, yearEnd ?? year);
        var rangeEnd = Math.Max(year, yearEnd ?? year);

        var filters = new List<FilterDefinition<GraphNode>> { Builders<GraphNode>.Filter.Eq(n => n.Type, type) };

        if (!string.IsNullOrWhiteSpace(semantic))
        {
            filters.Add(
                Builders<GraphNode>.Filter.ElemMatch(
                    n => n.TemporalFacets,
                    Builders<TemporalFacet>.Filter.And(
                        Builders<TemporalFacet>.Filter.Regex(f => f.Semantic, new BsonRegularExpression($"^{semantic}", "i")),
                        Builders<TemporalFacet>.Filter.Lte(f => f.Year, rangeEnd),
                        Builders<TemporalFacet>.Filter.Gte(f => f.Year, rangeStart)
                    )
                )
            );
        }
        else
        {
            filters.Add(Builders<GraphNode>.Filter.Lte(n => n.StartYear, rangeEnd));
            filters.Add(Builders<GraphNode>.Filter.Or(Builders<GraphNode>.Filter.Eq(n => n.EndYear, (int?)null), Builders<GraphNode>.Filter.Gte(n => n.EndYear, rangeStart)));
        }

        if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont))
            filters.Add(Builders<GraphNode>.Filter.Eq(n => n.Continuity, cont));

        return await _nodes.Find(Builders<GraphNode>.Filter.And(filters)).SortBy(n => n.Name).Limit(Math.Min(limit, 50)).ToListAsync(ct);
    }

    /// <summary>Get direct edges from an entity, optionally filtered by labels and continuity.</summary>
    public async Task<List<RelationshipEdge>> GetEdgesFromEntityAsync(int entityId, string? labelFilter, string? continuity, int limit, CancellationToken ct = default)
    {
        var filter = Builders<RelationshipEdge>.Filter.Eq(e => e.FromId, entityId);

        if (!string.IsNullOrWhiteSpace(labelFilter))
        {
            var labels = labelFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            filter &= Builders<RelationshipEdge>.Filter.In(e => e.Label, labels);
        }
        if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont))
            filter &= Builders<RelationshipEdge>.Filter.Eq(e => e.Continuity, cont);

        return await _edges.Find(filter).SortByDescending(e => e.Weight).Limit(Math.Min(limit, 50)).ToListAsync(ct);
    }

    /// <summary>
    /// Get all direct relationships for an entity — outgoing edges as stored plus
    /// inbound edges rewritten with their reverse label (so "X commanded_by Anakin"
    /// becomes "Anakin commanded X" on Anakin's results). Labels can be filtered by
    /// forward name (e.g. "commanded_by") or reverse name (e.g. "commanded"); the
    /// reverse form is resolved to the forward form automatically for the inbound query.
    /// </summary>
    public async Task<List<RelationshipEdge>> GetAllEdgesForEntityAsync(int entityId, string? labelFilter, string? continuity, int limit, CancellationToken ct = default)
    {
        // Build label filters for outgoing (forward labels) and inbound (forward labels
        // mapped from any requested reverses). Same logic as QueryGraphAsync.
        HashSet<string>? outgoingLabels = null;
        HashSet<string>? inboundForwardLabels = null;
        if (!string.IsNullOrWhiteSpace(labelFilter))
        {
            var reverseToForward = FieldSemantics
                .Relationships.Values.DistinctBy(d => d.Reverse)
                .Where(d => !string.IsNullOrEmpty(d.Reverse))
                .ToDictionary(d => d.Reverse, d => d.Label, StringComparer.OrdinalIgnoreCase);

            var labels = labelFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            outgoingLabels = [.. labels];
            inboundForwardLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in labels)
            {
                inboundForwardLabels.Add(reverseToForward.TryGetValue(l, out var fwd) ? fwd : l);
            }
        }

        var forwardToReverse = FieldSemantics.Relationships.Values.DistinctBy(d => d.Label).ToDictionary(d => d.Label, d => d.Reverse, StringComparer.OrdinalIgnoreCase);

        var outFilter = Builders<RelationshipEdge>.Filter.Eq(e => e.FromId, entityId);
        if (outgoingLabels is not null)
            outFilter &= Builders<RelationshipEdge>.Filter.In(e => e.Label, outgoingLabels);

        var inFilter = Builders<RelationshipEdge>.Filter.Eq(e => e.ToId, entityId);
        if (inboundForwardLabels is not null)
            inFilter &= Builders<RelationshipEdge>.Filter.In(e => e.Label, inboundForwardLabels);

        if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont))
        {
            var contFilter = Builders<RelationshipEdge>.Filter.Eq(e => e.Continuity, cont);
            outFilter &= contFilter;
            inFilter &= contFilter;
        }

        var halfLimit = Math.Max(1, Math.Min(limit, 100) / 2);
        var outTask = _edges.Find(outFilter).SortByDescending(e => e.Weight).Limit(halfLimit).ToListAsync(ct);
        var inTask = _edges.Find(inFilter).SortByDescending(e => e.Weight).Limit(halfLimit).ToListAsync(ct);
        await Task.WhenAll(outTask, inTask);

        var result = new List<RelationshipEdge>(outTask.Result);

        // Rewrite inbound edges: flip source/target and relabel to the reverse form,
        // so every returned edge reads naturally from the queried entity's perspective.
        foreach (var e in inTask.Result)
        {
            var reverseLabel = forwardToReverse.TryGetValue(e.Label, out var rev) && !string.IsNullOrEmpty(rev) ? rev : e.Label;

            result.Add(
                new RelationshipEdge
                {
                    Id = e.Id,
                    FromId = e.ToId,
                    FromName = e.ToName,
                    FromType = e.ToType,
                    ToId = e.FromId,
                    ToName = e.FromName,
                    ToType = e.FromType,
                    Label = reverseLabel,
                    Weight = e.Weight,
                    Evidence = e.Evidence,
                    SourcePageId = e.SourcePageId,
                    Continuity = e.Continuity,
                    PairId = e.PairId,
                    CreatedAt = e.CreatedAt,
                    FromYear = e.FromYear,
                    ToYear = e.ToYear,
                }
            );
        }

        return result.OrderByDescending(e => e.Weight).Take(Math.Min(limit, 100)).ToList();
    }

    /// <summary>Get relationship type summary (label + count + avgWeight) for an entity.</summary>
    public async Task<List<(string label, int count, double avgWeight)>> GetRelationshipTypesAsync(int entityId, string? continuity, CancellationToken ct = default)
    {
        var matchFilter = new BsonDocument(RelationshipEdgeBsonFields.FromId, entityId);
        if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont))
            matchFilter[GraphNodeBsonFields.Continuity] = cont.ToString();

        var pipeline = new[]
        {
            new BsonDocument("$match", matchFilter),
            new BsonDocument(
                "$group",
                new BsonDocument
                {
                    { MongoFields.Id, "$" + RelationshipEdgeBsonFields.Label },
                    { "count", new BsonDocument("$sum", 1) },
                    { "avgWeight", new BsonDocument("$avg", "$" + RelationshipEdgeBsonFields.Weight) },
                }
            ),
            new BsonDocument("$sort", new BsonDocument("count", -1)),
        };

        var results = await _edges.Database.GetCollection<BsonDocument>(Collections.KgEdges).Aggregate<BsonDocument>(pipeline).ToListAsync(ct);

        return results.Select(d => (label: d[MongoFields.Id].AsString, count: d["count"].AsInt32, avgWeight: d["avgWeight"].AsDouble)).ToList();
    }

    /// <summary>Bidirectional BFS to find the shortest path between two entities.</summary>
    public async Task<(bool connected, List<(int from, int to, string label, string evidence, string fromName, string toName)> path)> FindConnectionsAsync(
        int entityId1,
        int entityId2,
        int maxHops,
        string? continuity,
        int? yearFrom = null,
        int? yearTo = null,
        CancellationToken ct = default
    )
    {
        maxHops = Math.Clamp(maxHops, 1, 4);

        FilterDefinition<RelationshipEdge>? contFilter = null;
        if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont))
            contFilter = Builders<RelationshipEdge>.Filter.Eq(e => e.Continuity, cont);

        // Temporal window filter — same semantics as QueryGraphAsync: recall-biased
        // (null edge bounds pass through), AND-combined with the continuity filter.
        FilterDefinition<RelationshipEdge>? temporalFilter = null;
        if (yearFrom.HasValue || yearTo.HasValue)
        {
            var b = Builders<RelationshipEdge>.Filter;
            var clauses = new List<FilterDefinition<RelationshipEdge>>();
            if (yearTo.HasValue)
                clauses.Add(b.Or(b.Eq(e => e.FromYear, null), b.Lte(e => e.FromYear, yearTo.Value)));
            if (yearFrom.HasValue)
                clauses.Add(b.Or(b.Eq(e => e.ToYear, null), b.Gte(e => e.ToYear, yearFrom.Value)));
            temporalFilter = b.And(clauses);
        }

        var edgeFilter = contFilter is not null && temporalFilter is not null ? Builders<RelationshipEdge>.Filter.And(contFilter, temporalFilter) : contFilter ?? temporalFilter;

        if (entityId1 == entityId2)
            return (true, []);

        var frontier1 = new HashSet<int> { entityId1 };
        var frontier2 = new HashSet<int> { entityId2 };
        var visited1 = new Dictionary<int, (int parent, string label, string evidence, string fromName, string toName)>();
        var visited2 = new Dictionary<int, (int parent, string label, string evidence, string fromName, string toName)>();
        visited1[entityId1] = (-1, "", "", "", "");
        visited2[entityId2] = (-1, "", "", "", "");

        int? meetingPoint = null;

        for (int hop = 0; hop < maxHops && meetingPoint is null; hop++)
        {
            if (frontier1.Count <= frontier2.Count)
            {
                frontier1 = await ExpandFrontierAsync(frontier1, visited1, edgeFilter, ct);
                meetingPoint = frontier1.FirstOrDefault(id => visited2.ContainsKey(id));
                if (meetingPoint == 0 && !visited2.ContainsKey(0))
                    meetingPoint = null;
            }
            else
            {
                frontier2 = await ExpandFrontierAsync(frontier2, visited2, edgeFilter, ct);
                meetingPoint = frontier2.FirstOrDefault(id => visited1.ContainsKey(id));
                if (meetingPoint == 0 && !visited1.ContainsKey(0))
                    meetingPoint = null;
            }

            if (frontier1.Count == 0 && frontier2.Count == 0)
                break;
        }

        if (meetingPoint is null)
            return (false, []);

        var path = new List<(int from, int to, string label, string evidence, string fromName, string toName)>();

        var current = meetingPoint.Value;
        var pathFromE1 = new List<(int from, int to, string label, string evidence, string fromName, string toName)>();
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
        Dictionary<int, (int parent, string label, string evidence, string fromName, string toName)> visited,
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
                visited[edge.ToId] = (edge.FromId, edge.Label, edge.Evidence, edge.FromName, edge.ToName);
                newFrontier.Add(edge.ToId);
            }
        }
        return newFrontier;
    }

    // ── KG Analytics aggregation methods ──

    /// <summary>
    /// Count entities of <paramref name="relatedType"/> connected to each entity of
    /// <paramref name="entityType"/> via <paramref name=RelationshipEdgeBsonFields.Label/> edges.
    /// E.g. "How many Battle nodes connect to each War node via 'battle_in'?"
    /// </summary>
    public async Task<List<(string name, int id, int count)>> CountRelatedEntitiesAsync(
        string entityType,
        string relatedType,
        string label,
        bool groupBySource,
        string? continuity,
        int limit,
        CancellationToken ct = default
    )
    {
        limit = Math.Clamp(limit, 1, 50);

        var match = new BsonDocument { { RelationshipEdgeBsonFields.Label, label } };
        if (groupBySource)
        {
            match[RelationshipEdgeBsonFields.FromType] = entityType;
            match[RelationshipEdgeBsonFields.ToType] = relatedType;
        }
        else
        {
            match[RelationshipEdgeBsonFields.FromType] = relatedType;
            match[RelationshipEdgeBsonFields.ToType] = entityType;
        }
        if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont))
            match[GraphNodeBsonFields.Continuity] = cont.ToString();

        var groupField = groupBySource ? RelationshipEdgeBsonFields.FromName : RelationshipEdgeBsonFields.ToName;
        var groupIdField = groupBySource ? RelationshipEdgeBsonFields.FromId : RelationshipEdgeBsonFields.ToId;

        var pipeline = new[]
        {
            new BsonDocument("$match", match),
            new BsonDocument(
                "$group",
                new BsonDocument
                {
                    {
                        MongoFields.Id,
                        new BsonDocument { { "name", $"${groupField}" }, { "id", $"${groupIdField}" } }
                    },
                    { "count", new BsonDocument("$sum", 1) },
                }
            ),
            new BsonDocument("$sort", new BsonDocument("count", -1)),
            new BsonDocument("$limit", limit),
        };

        var results = await _edges.Database.GetCollection<BsonDocument>(Collections.KgEdges).Aggregate<BsonDocument>(pipeline).ToListAsync(ct);

        return results.Select(d => (name: d[MongoFields.Id]["name"].AsString, id: d[MongoFields.Id]["id"].AsInt32, count: d["count"].AsInt32)).ToList();
    }

    /// <summary>
    /// Count nodes grouped by a property value.
    /// E.g. "Characters grouped by species" or "Starships grouped by manufacturer".
    /// </summary>
    public async Task<List<BsonDocument>> CountNodesByPropertiesAsync(string entityType, List<string> properties, string? continuity, bool includeExample, int limit, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 50);

        var match = new BsonDocument(GraphNodeBsonFields.Type, entityType);
        if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont))
            match[GraphNodeBsonFields.Continuity] = cont.ToString();

        // Build $group _id from multiple properties
        var groupId = new BsonDocument();
        foreach (var prop in properties)
            groupId[prop] = new BsonDocument("$ifNull", new BsonArray { $"$properties.{prop}", "Unknown" });

        var group = new BsonDocument { [MongoFields.Id] = groupId, ["count"] = new BsonDocument("$sum", 1) };

        if (includeExample)
        {
            group["exampleTitle"] = new BsonDocument("$first", "$title");
            group["examplePageId"] = new BsonDocument("$first", "$pageId");
        }

        var pipeline = new List<BsonDocument> { new("$match", match), new("$group", group), new("$sort", new BsonDocument("count", -1)), new("$limit", limit) };

        return await _nodes.Database.GetCollection<BsonDocument>(Collections.KgNodes).Aggregate<BsonDocument>(pipeline.ToArray()).ToListAsync(ct);
    }

    public async Task<List<(string value, int count)>> CountNodesByPropertyAsync(string entityType, string property, string? continuity, int limit, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 50);

        var match = new BsonDocument(GraphNodeBsonFields.Type, entityType);
        if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont))
            match[GraphNodeBsonFields.Continuity] = cont.ToString();

        var pipeline = new[]
        {
            new BsonDocument("$match", match),
            new BsonDocument("$unwind", $"$properties.{property}"),
            new BsonDocument("$group", new BsonDocument { { MongoFields.Id, $"$properties.{property}" }, { "count", new BsonDocument("$sum", 1) } }),
            new BsonDocument("$sort", new BsonDocument("count", -1)),
            new BsonDocument("$limit", limit),
        };

        var results = await _nodes.Database.GetCollection<BsonDocument>(Collections.KgNodes).Aggregate<BsonDocument>(pipeline).ToListAsync(ct);

        return results.Select(d => (value: d[MongoFields.Id].AsString, count: d["count"].AsInt32)).ToList();
    }

    /// <summary>
    /// Count nodes bucketed by year ranges, optionally filtered by temporal facet semantic dimension.
    /// Without semantic: uses flat startYear envelope (e.g. "Battles per year").
    /// With semantic: unwinds temporalFacets and groups by the facet's year
    /// (e.g. "Characters who died per year" → semantic="lifespan.end").
    /// </summary>
    public async Task<List<(int year, int count)>> CountByYearRangeAsync(
        string entityType,
        int startYear,
        int endYear,
        int bucket,
        string? continuity,
        string? semantic = null,
        CancellationToken ct = default
    )
    {
        bucket = Math.Max(bucket, 1);
        var rangeStart = Math.Min(startYear, endYear);
        var rangeEnd = Math.Max(startYear, endYear);

        var pipeline = new List<BsonDocument>();

        if (!string.IsNullOrWhiteSpace(semantic))
        {
            // Semantic path: unwind temporalFacets, match on semantic prefix + year range
            var match = new BsonDocument(GraphNodeBsonFields.Type, entityType);
            if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont1))
                match[GraphNodeBsonFields.Continuity] = cont1.ToString();

            pipeline.Add(new BsonDocument("$match", match));
            pipeline.Add(new BsonDocument("$unwind", "$" + GraphNodeBsonFields.TemporalFacets));
            pipeline.Add(
                new BsonDocument(
                    "$match",
                    new BsonDocument
                    {
                        { GraphNodeBsonFields.TemporalFacetSemantic, new BsonDocument("$regex", $"^{semantic}").Add("$options", "i") },
                        {
                            GraphNodeBsonFields.TemporalFacetYear,
                            new BsonDocument { { "$gte", rangeStart }, { "$lte", rangeEnd } }
                        },
                    }
                )
            );
            pipeline.Add(
                new BsonDocument(
                    "$group",
                    new BsonDocument
                    {
                        {
                            MongoFields.Id,
                            new BsonDocument("$multiply", new BsonArray { new BsonDocument("$floor", new BsonDocument("$divide", new BsonArray { "$temporalFacets.year", bucket })), bucket })
                        },
                        { "count", new BsonDocument("$sum", 1) },
                    }
                )
            );
        }
        else
        {
            // Flat envelope path: use startYear directly
            var match = new BsonDocument
            {
                { GraphNodeBsonFields.Type, entityType },
                {
                    GraphNodeBsonFields.StartYear,
                    new BsonDocument { { "$gte", rangeStart }, { "$lte", rangeEnd } }
                },
            };
            if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont2))
                match[GraphNodeBsonFields.Continuity] = cont2.ToString();

            pipeline.Add(new BsonDocument("$match", match));
            pipeline.Add(
                new BsonDocument(
                    "$group",
                    new BsonDocument
                    {
                        {
                            MongoFields.Id,
                            new BsonDocument(
                                "$multiply",
                                new BsonArray { new BsonDocument("$floor", new BsonDocument("$divide", new BsonArray { "$" + GraphNodeBsonFields.StartYear, bucket })), bucket }
                            )
                        },
                        { "count", new BsonDocument("$sum", 1) },
                    }
                )
            );
        }

        pipeline.Add(new BsonDocument("$sort", new BsonDocument(MongoFields.Id, 1)));

        var results = await _nodes.Database.GetCollection<BsonDocument>(Collections.KgNodes).Aggregate<BsonDocument>(pipeline).ToListAsync(ct);

        return results.Select(d => (year: d[MongoFields.Id].ToInt32(), count: d["count"].AsInt32)).ToList();
    }

    /// <summary>
    /// Count edges between specific entity type pairs, grouped by label.
    /// E.g. "How are Wars and Characters connected?" → label distribution.
    /// </summary>
    public async Task<List<(string label, int count)>> CountEdgesBetweenTypesAsync(string fromType, string toType, string? continuity, int limit, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 50);

        var match = new BsonDocument { { RelationshipEdgeBsonFields.FromType, fromType }, { RelationshipEdgeBsonFields.ToType, toType } };
        if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont))
            match[GraphNodeBsonFields.Continuity] = cont.ToString();

        var pipeline = new[]
        {
            new BsonDocument("$match", match),
            new BsonDocument("$group", new BsonDocument { { MongoFields.Id, "$" + RelationshipEdgeBsonFields.Label }, { "count", new BsonDocument("$sum", 1) } }),
            new BsonDocument("$sort", new BsonDocument("count", -1)),
            new BsonDocument("$limit", limit),
        };

        var results = await _edges.Database.GetCollection<BsonDocument>(Collections.KgEdges).Aggregate<BsonDocument>(pipeline).ToListAsync(ct);

        return results.Select(d => (label: d[MongoFields.Id].AsString, count: d["count"].AsInt32)).ToList();
    }

    /// <summary>
    /// Rank entities by total edge degree (outgoing edges).
    /// E.g. "Most connected characters" or "Most referenced planets".
    /// </summary>
    public async Task<List<(string name, int id, int degree)>> TopConnectedEntitiesAsync(string? entityType, string? label, string? continuity, int limit, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 50);

        var match = new BsonDocument();
        if (!string.IsNullOrWhiteSpace(entityType))
            match[RelationshipEdgeBsonFields.FromType] = entityType;
        if (!string.IsNullOrWhiteSpace(label))
            match[RelationshipEdgeBsonFields.Label] = label;
        if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont))
            match[GraphNodeBsonFields.Continuity] = cont.ToString();

        var pipeline = new List<BsonDocument>();
        if (match.ElementCount > 0)
            pipeline.Add(new BsonDocument("$match", match));

        pipeline.AddRange(
            [
                new BsonDocument(
                    "$group",
                    new BsonDocument
                    {
                        {
                            MongoFields.Id,
                            new BsonDocument { { "name", "$" + RelationshipEdgeBsonFields.FromName }, { "id", "$" + RelationshipEdgeBsonFields.FromId } }
                        },
                        { "degree", new BsonDocument("$sum", 1) },
                    }
                ),
                new BsonDocument("$sort", new BsonDocument("degree", -1)),
                new BsonDocument("$limit", limit),
            ]
        );

        var results = await _edges.Database.GetCollection<BsonDocument>(Collections.KgEdges).Aggregate<BsonDocument>(pipeline).ToListAsync(ct);

        return results.Select(d => (name: d[MongoFields.Id]["name"].AsString, id: d[MongoFields.Id]["id"].AsInt32, degree: d["degree"].AsInt32)).ToList();
    }

    /// <summary>
    /// Multi-dimension profile for an entity — counts edges by multiple labels.
    /// Powers Radar charts: e.g. Clone Wars → battles: 47, combatants: 230, planets: 23.
    /// </summary>
    public async Task<List<(string dimension, int count)>> EntityProfileAsync(int entityId, List<string> labels, string? continuity, CancellationToken ct = default)
    {
        var match = new BsonDocument { { RelationshipEdgeBsonFields.FromId, entityId }, { RelationshipEdgeBsonFields.Label, new BsonDocument("$in", new BsonArray(labels)) } };
        if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont))
            match[GraphNodeBsonFields.Continuity] = cont.ToString();

        var pipeline = new[]
        {
            new BsonDocument("$match", match),
            new BsonDocument("$group", new BsonDocument { { MongoFields.Id, "$" + RelationshipEdgeBsonFields.Label }, { "count", new BsonDocument("$sum", 1) } }),
            new BsonDocument("$sort", new BsonDocument(MongoFields.Id, 1)),
        };

        var results = await _edges.Database.GetCollection<BsonDocument>(Collections.KgEdges).Aggregate<BsonDocument>(pipeline).ToListAsync(ct);

        // Return in the order of the requested labels, with 0 for missing
        var map = results.ToDictionary(d => d[MongoFields.Id].AsString, d => d["count"].AsInt32);
        return labels.Select(l => (dimension: l, count: map.GetValueOrDefault(l, 0))).ToList();
    }

    /// <summary>
    /// Count nodes grouped by entity type.
    /// E.g. overall KG composition: Character: 8200, Planet: 1100, etc.
    /// </summary>
    public async Task<List<(string type, int count)>> CountNodesByTypeAsync(string? continuity, int limit, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 50);

        var match = new BsonDocument();
        if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont))
            match[GraphNodeBsonFields.Continuity] = cont.ToString();

        var pipeline = new[]
        {
            new BsonDocument("$match", match),
            new BsonDocument("$group", new BsonDocument { { MongoFields.Id, "$" + GraphNodeBsonFields.Type }, { "count", new BsonDocument("$sum", 1) } }),
            new BsonDocument("$sort", new BsonDocument("count", -1)),
            new BsonDocument("$limit", limit),
        };

        var results = await _nodes.Database.GetCollection<BsonDocument>(Collections.KgNodes).Aggregate<BsonDocument>(pipeline).ToListAsync(ct);

        return results.Select(d => (type: d[MongoFields.Id].AsString, count: d["count"].AsInt32)).ToList();
    }

    /// <summary>
    /// For a specific entity, find its connected entities via a label, then group those
    /// connected entities by a property value. E.g. "Species breakdown of Jedi Order members":
    /// root=Jedi Order, label=member_of (edges FROM members TO Jedi Order), property=Species.
    /// </summary>
    public async Task<List<(string value, int count)>> CountPropertyForRelatedEntitiesAsync(
        int rootEntityId,
        string label,
        string property,
        bool rootIsTarget,
        string? continuity,
        int limit,
        CancellationToken ct = default
    )
    {
        limit = Math.Clamp(limit, 1, 50);

        // Step 1: find connected entity IDs via the edge label
        var edgeMatch = new BsonDocument(RelationshipEdgeBsonFields.Label, label);
        if (rootIsTarget)
            edgeMatch[RelationshipEdgeBsonFields.ToId] = rootEntityId;
        else
            edgeMatch[RelationshipEdgeBsonFields.FromId] = rootEntityId;
        if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont))
            edgeMatch[GraphNodeBsonFields.Continuity] = cont.ToString();

        var connectedField = rootIsTarget ? RelationshipEdgeBsonFields.FromId : RelationshipEdgeBsonFields.ToId;

        var edgePipeline = new[] { new BsonDocument("$match", edgeMatch), new BsonDocument("$group", new BsonDocument(MongoFields.Id, $"${connectedField}")) };

        var edgeResults = await _edges.Database.GetCollection<BsonDocument>(Collections.KgEdges).Aggregate<BsonDocument>(edgePipeline).ToListAsync(ct);

        var connectedIds = edgeResults.Select(d => d[MongoFields.Id].AsInt32).ToList();
        if (connectedIds.Count == 0)
            return [];

        // Step 2: aggregate connected nodes by the property value
        var nodePipeline = new[]
        {
            new BsonDocument("$match", new BsonDocument(MongoFields.Id, new BsonDocument("$in", new BsonArray(connectedIds)))),
            new BsonDocument("$unwind", $"$properties.{property}"),
            new BsonDocument("$group", new BsonDocument { { MongoFields.Id, $"$properties.{property}" }, { "count", new BsonDocument("$sum", 1) } }),
            new BsonDocument("$sort", new BsonDocument("count", -1)),
            new BsonDocument("$limit", limit),
        };

        var results = await _nodes.Database.GetCollection<BsonDocument>(Collections.KgNodes).Aggregate<BsonDocument>(nodePipeline).ToListAsync(ct);

        return results.Select(d => (value: d[MongoFields.Id].AsString, count: d["count"].AsInt32)).ToList();
    }

    /// <summary>
    /// Group source entities by the target entity they connect to via a label.
    /// E.g. "Characters grouped by faction" → label=member_of, sourceType=Character
    /// → returns [{name: "Galactic Empire", count: 450}, {name: "Jedi Order", count: 230}, ...].
    /// </summary>
    public async Task<List<(string name, int id, int count)>> GroupEntitiesByConnectionAsync(
        string sourceType,
        string label,
        string? targetType,
        string? continuity,
        int limit,
        CancellationToken ct = default
    )
    {
        limit = Math.Clamp(limit, 1, 50);

        var match = new BsonDocument { { RelationshipEdgeBsonFields.FromType, sourceType }, { RelationshipEdgeBsonFields.Label, label } };
        if (!string.IsNullOrWhiteSpace(targetType))
            match[RelationshipEdgeBsonFields.ToType] = targetType;
        if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont))
            match[GraphNodeBsonFields.Continuity] = cont.ToString();

        var pipeline = new[]
        {
            new BsonDocument("$match", match),
            new BsonDocument(
                "$group",
                new BsonDocument
                {
                    {
                        MongoFields.Id,
                        new BsonDocument { { "name", "$" + RelationshipEdgeBsonFields.ToName }, { "id", "$" + RelationshipEdgeBsonFields.ToId } }
                    },
                    { "count", new BsonDocument("$sum", 1) },
                }
            ),
            new BsonDocument("$sort", new BsonDocument("count", -1)),
            new BsonDocument("$limit", limit),
        };

        var results = await _edges.Database.GetCollection<BsonDocument>(Collections.KgEdges).Aggregate<BsonDocument>(pipeline).ToListAsync(ct);

        return results.Select(d => (name: d[MongoFields.Id]["name"].AsString, id: d[MongoFields.Id]["id"].AsInt32, count: d["count"].AsInt32)).ToList();
    }

    /// <summary>
    /// Batch entity_profile for multiple entities — returns counts per label per entity.
    /// Powers side-by-side Radar/StackedBar: "Compare Yoda, Palpatine, Dooku across dimensions".
    /// </summary>
    public async Task<List<(int entityId, string entityName, List<(string label, int count)> dimensions)>> CompareEntitiesAsync(
        List<int> entityIds,
        List<string> labels,
        string? continuity,
        CancellationToken ct = default
    )
    {
        if (entityIds.Count > 10)
            entityIds = entityIds.Take(10).ToList();

        var match = new BsonDocument
        {
            { RelationshipEdgeBsonFields.FromId, new BsonDocument("$in", new BsonArray(entityIds)) },
            { RelationshipEdgeBsonFields.Label, new BsonDocument("$in", new BsonArray(labels)) },
        };
        if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont))
            match[GraphNodeBsonFields.Continuity] = cont.ToString();

        var pipeline = new[]
        {
            new BsonDocument("$match", match),
            new BsonDocument(
                "$group",
                new BsonDocument
                {
                    {
                        MongoFields.Id,
                        new BsonDocument
                        {
                            { "entityId", "$" + RelationshipEdgeBsonFields.FromId },
                            { "entityName", "$" + RelationshipEdgeBsonFields.FromName },
                            { RelationshipEdgeBsonFields.Label, "$" + RelationshipEdgeBsonFields.Label },
                        }
                    },
                    { "count", new BsonDocument("$sum", 1) },
                }
            ),
        };

        var results = await _edges.Database.GetCollection<BsonDocument>(Collections.KgEdges).Aggregate<BsonDocument>(pipeline).ToListAsync(ct);

        // Group by entity, fill in zeros for missing labels
        var grouped = results
            .GroupBy(d => d[MongoFields.Id]["entityId"].AsInt32)
            .Select(g =>
            {
                var entityId = g.Key;
                var entityName = g.First()[MongoFields.Id]["entityName"].AsString;
                var counts = g.ToDictionary(d => d[MongoFields.Id][RelationshipEdgeBsonFields.Label].AsString, d => d["count"].AsInt32);
                var dimensions = labels.Select(l => (label: l, count: counts.GetValueOrDefault(l, 0))).ToList();
                return (entityId, entityName, dimensions);
            })
            .ToList();

        // Include entities with zero edges (not in results at all)
        var foundIds = grouped.Select(g => g.entityId).ToHashSet();
        foreach (var id in entityIds.Where(id => !foundIds.Contains(id)))
        {
            var node = await GetNodeByIdAsync(id, ct);
            grouped.Add((id, node?.Name ?? $"#{id}", labels.Select(l => (label: l, count: 0)).ToList()));
        }

        // Preserve the requested order
        return entityIds.Select(id => grouped.First(g => g.entityId == id)).ToList();
    }

    /// <summary>
    /// Find entities whose temporal facets match a specific semantic dimension.role (e.g.
    /// "institutional.reorganized", "lifespan.end") within a year range. Uses $elemMatch so
    /// each returned node comes with the exact facet that caused the match.
    /// </summary>
    public async Task<List<(int pageId, string name, string type, string semantic, int year, string text, string calendar)>> FindByLifecycleTransitionAsync(
        string semantic,
        int startYear,
        int endYear,
        string? entityType,
        string? continuity,
        int limit,
        CancellationToken ct = default
    )
    {
        var rangeStart = Math.Min(startYear, endYear);
        var rangeEnd = Math.Max(startYear, endYear);
        limit = Math.Clamp(limit, 1, 100);

        var match = new BsonDocument();
        if (!string.IsNullOrWhiteSpace(entityType))
            match[GraphNodeBsonFields.Type] = entityType;
        if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont))
            match[GraphNodeBsonFields.Continuity] = cont.ToString();

        // Pre-filter to nodes containing at least one matching facet to limit the $unwind fanout.
        match[GraphNodeBsonFields.TemporalFacets] = new BsonDocument(
            "$elemMatch",
            new BsonDocument
            {
                { "semantic", new BsonDocument("$regex", $"^{Regex.Escape(semantic)}$").Add("$options", "i") },
                {
                    "year",
                    new BsonDocument { { "$gte", rangeStart }, { "$lte", rangeEnd } }
                },
            }
        );

        var pipeline = new[]
        {
            new BsonDocument("$match", match),
            new BsonDocument("$unwind", "$" + GraphNodeBsonFields.TemporalFacets),
            new BsonDocument(
                "$match",
                new BsonDocument
                {
                    { GraphNodeBsonFields.TemporalFacetSemantic, new BsonDocument("$regex", $"^{Regex.Escape(semantic)}$").Add("$options", "i") },
                    {
                        GraphNodeBsonFields.TemporalFacetYear,
                        new BsonDocument { { "$gte", rangeStart }, { "$lte", rangeEnd } }
                    },
                }
            ),
            new BsonDocument("$sort", new BsonDocument(GraphNodeBsonFields.TemporalFacetYear, 1)),
            new BsonDocument("$limit", limit),
            new BsonDocument(
                "$project",
                new BsonDocument
                {
                    // GraphNode.PageId is [BsonId] so the source field is _id — alias it as pageId in the output.
                    { "pageId", "$" + MongoFields.Id },
                    { GraphNodeBsonFields.Name, "$" + GraphNodeBsonFields.Name },
                    { GraphNodeBsonFields.Type, "$" + GraphNodeBsonFields.Type },
                    { "semantic", "$" + GraphNodeBsonFields.TemporalFacets + "." + TemporalFacetBsonFields.Semantic },
                    { "year", "$" + GraphNodeBsonFields.TemporalFacets + "." + TemporalFacetBsonFields.Year },
                    { "text", "$" + GraphNodeBsonFields.TemporalFacets + "." + TemporalFacetBsonFields.Text },
                    { "calendar", "$" + GraphNodeBsonFields.TemporalFacets + "." + TemporalFacetBsonFields.Calendar },
                    { MongoFields.Id, 0 },
                }
            ),
        };

        var results = await _nodes.Database.GetCollection<BsonDocument>(Collections.KgNodes).Aggregate<BsonDocument>(pipeline, cancellationToken: ct).ToListAsync(ct);

        return results
            .Select(d =>
                (
                    pageId: d.GetValue("pageId", 0).ToInt32(),
                    name: d.GetValue(GraphNodeBsonFields.Name, "").AsString,
                    type: d.GetValue(GraphNodeBsonFields.Type, "").AsString,
                    semantic: d.GetValue("semantic", "").AsString,
                    year: d.GetValue("year", 0).ToInt32(),
                    text: d.GetValue("text", "").AsString,
                    calendar: d.GetValue("calendar", "").AsString
                )
            )
            .ToList();
    }

    /// <summary>
    /// Count lifecycle transitions per year bucket by unwinding temporal facets and grouping
    /// on year. More accurate than CountByYearRangeAsync for lifecycle events because it
    /// counts facet hits directly rather than using the node's startYear envelope.
    /// </summary>
    public async Task<List<(int year, int count)>> CountLifecycleTransitionsAsync(
        string semantic,
        int startYear,
        int endYear,
        int bucket,
        string? entityType,
        string? continuity,
        CancellationToken ct = default
    )
    {
        bucket = Math.Max(bucket, 1);
        var rangeStart = Math.Min(startYear, endYear);
        var rangeEnd = Math.Max(startYear, endYear);

        var match = new BsonDocument();
        if (!string.IsNullOrWhiteSpace(entityType))
            match[GraphNodeBsonFields.Type] = entityType;
        if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont))
            match[GraphNodeBsonFields.Continuity] = cont.ToString();

        match[GraphNodeBsonFields.TemporalFacets] = new BsonDocument(
            "$elemMatch",
            new BsonDocument
            {
                { "semantic", new BsonDocument("$regex", $"^{Regex.Escape(semantic)}$").Add("$options", "i") },
                {
                    "year",
                    new BsonDocument { { "$gte", rangeStart }, { "$lte", rangeEnd } }
                },
            }
        );

        var pipeline = new[]
        {
            new BsonDocument("$match", match),
            new BsonDocument("$unwind", "$" + GraphNodeBsonFields.TemporalFacets),
            new BsonDocument(
                "$match",
                new BsonDocument
                {
                    { GraphNodeBsonFields.TemporalFacetSemantic, new BsonDocument("$regex", $"^{Regex.Escape(semantic)}$").Add("$options", "i") },
                    {
                        GraphNodeBsonFields.TemporalFacetYear,
                        new BsonDocument { { "$gte", rangeStart }, { "$lte", rangeEnd } }
                    },
                }
            ),
            new BsonDocument(
                "$group",
                new BsonDocument
                {
                    {
                        MongoFields.Id,
                        new BsonDocument("$multiply", new BsonArray { new BsonDocument("$floor", new BsonDocument("$divide", new BsonArray { "$temporalFacets.year", bucket })), bucket })
                    },
                    { "count", new BsonDocument("$sum", 1) },
                }
            ),
            new BsonDocument("$sort", new BsonDocument(MongoFields.Id, 1)),
        };

        var results = await _nodes.Database.GetCollection<BsonDocument>(Collections.KgNodes).Aggregate<BsonDocument>(pipeline, cancellationToken: ct).ToListAsync(ct);

        return results.Select(d => (year: d[MongoFields.Id].ToInt32(), count: d["count"].AsInt32)).ToList();
    }

    /// <summary>
    /// Browse aggregated statistics per relationship label in kg.edges. Each row is one
    /// directed label with overall count, Canon/Legends split, avg confidence, top source
    /// and target entity types, and a sample edge. Used by the Knowledge Graph page's
    /// "Edge Labels" tab for discovering which relationship kinds exist in the graph.
    /// </summary>
    public async Task<BrowseEdgeLabelsResult> BrowseEdgeLabelsAsync(
        string? q,
        string? continuity,
        string? fromType,
        string? toType,
        long minCount,
        int page,
        int pageSize,
        string? sortBy,
        string? sortDirection,
        string? realm,
        CancellationToken ct
    )
    {
        if (pageSize > 100)
            pageSize = 100;
        if (page < 1)
            page = 1;

        var match = new BsonDocument();
        if (!string.IsNullOrWhiteSpace(q))
            match[RelationshipEdgeBsonFields.Label] = new BsonDocument { { "$regex", q }, { "$options", "i" } };
        if (!string.IsNullOrWhiteSpace(continuity) && Enum.TryParse<Continuity>(continuity, true, out var cont))
            match[RelationshipEdgeBsonFields.Continuity] = cont.ToString();
        if (!string.IsNullOrWhiteSpace(fromType))
            match[RelationshipEdgeBsonFields.FromType] = fromType;
        if (!string.IsNullOrWhiteSpace(toType))
            match[RelationshipEdgeBsonFields.ToType] = toType;

        var sortField = sortBy switch
        {
            "label" => MongoFields.Id,
            "avgWeight" => "avgWeight",
            _ => "count",
        };
        var sortDir = sortDirection == "ascending" ? 1 : -1;

        var pipeline = new List<BsonDocument>();
        if (match.ElementCount > 0)
            pipeline.Add(new BsonDocument("$match", match));

        // Realm filter: edges don't carry Realm directly, so $lookup the source
        // node from kg.nodes and keep edges whose source node matches the selected
        // realm (plus Unknown). Applied after the cheap $match so the join set is
        // as small as possible.
        if (!string.IsNullOrWhiteSpace(realm) && Enum.TryParse<Realm>(realm, true, out var r))
        {
            pipeline.Add(
                new BsonDocument(
                    "$lookup",
                    new BsonDocument
                    {
                        { "from", Collections.KgNodes },
                        { "localField", RelationshipEdgeBsonFields.FromId },
                        { "foreignField", MongoFields.Id },
                        { "as", "_fromNode" },
                        {
                            "pipeline",
                            new BsonArray { new BsonDocument("$project", new BsonDocument(GraphNodeBsonFields.Realm, 1)) }
                        },
                    }
                )
            );
            pipeline.Add(new BsonDocument("$match", new BsonDocument("_fromNode." + GraphNodeBsonFields.Realm, new BsonDocument("$in", new BsonArray { r.ToString(), nameof(Realm.Unknown) }))));
        }

        // Two-stage group to avoid pushing all edges into a single document (16MB BSON limit).
        // Stage 1: group by (label, fromType, toType) to get per-triple counts — small result set.
        pipeline.Add(
            new BsonDocument(
                "$group",
                new BsonDocument
                {
                    {
                        MongoFields.Id,
                        new BsonDocument
                        {
                            { "label", "$" + RelationshipEdgeBsonFields.Label },
                            { "fromType", "$" + RelationshipEdgeBsonFields.FromType },
                            { "toType", "$" + RelationshipEdgeBsonFields.ToType },
                        }
                    },
                    { "count", new BsonDocument("$sum", 1) },
                    {
                        "canonCount",
                        new BsonDocument(
                            "$sum",
                            new BsonDocument("$cond", new BsonArray { new BsonDocument("$eq", new BsonArray { "$" + RelationshipEdgeBsonFields.Continuity, nameof(Continuity.Canon) }), 1, 0 })
                        )
                    },
                    {
                        "legendsCount",
                        new BsonDocument(
                            "$sum",
                            new BsonDocument("$cond", new BsonArray { new BsonDocument("$eq", new BsonArray { "$" + RelationshipEdgeBsonFields.Continuity, nameof(Continuity.Legends) }), 1, 0 })
                        )
                    },
                    { "avgWeight", new BsonDocument("$avg", "$" + RelationshipEdgeBsonFields.Weight) },
                    {
                        "sample",
                        new BsonDocument(
                            "$first",
                            new BsonDocument
                            {
                                { "fromId", "$" + RelationshipEdgeBsonFields.FromId },
                                { "fromName", "$" + RelationshipEdgeBsonFields.FromName },
                                { "toId", "$" + RelationshipEdgeBsonFields.ToId },
                                { "toName", "$" + RelationshipEdgeBsonFields.ToName },
                            }
                        )
                    },
                }
            )
        );

        // Stage 2: re-group by label, rolling up type counts and merging stats.
        pipeline.Add(
            new BsonDocument(
                "$group",
                new BsonDocument
                {
                    { MongoFields.Id, "$_id.label" },
                    { "count", new BsonDocument("$sum", "$count") },
                    { "canonCount", new BsonDocument("$sum", "$canonCount") },
                    { "legendsCount", new BsonDocument("$sum", "$legendsCount") },
                    { "avgWeight", new BsonDocument("$avg", "$avgWeight") },
                    { "fromTypes", new BsonDocument("$push", new BsonDocument { { "type", "$_id.fromType" }, { "count", "$count" } }) },
                    { "toTypes", new BsonDocument("$push", new BsonDocument { { "type", "$_id.toType" }, { "count", "$count" } }) },
                    { "sample", new BsonDocument("$first", "$sample") },
                }
            )
        );

        if (minCount > 0)
            pipeline.Add(new BsonDocument("$match", new BsonDocument("count", new BsonDocument("$gte", minCount))));

        pipeline.Add(
            new BsonDocument(
                "$facet",
                new BsonDocument
                {
                    {
                        "total",
                        new BsonArray { new BsonDocument("$count", "n") }
                    },
                    {
                        "items",
                        new BsonArray { new BsonDocument("$sort", new BsonDocument(sortField, sortDir)), new BsonDocument("$skip", (page - 1) * pageSize), new BsonDocument("$limit", pageSize) }
                    },
                }
            )
        );

        var result = await _edges.Database.GetCollection<BsonDocument>(Collections.KgEdges).Aggregate<BsonDocument>(pipeline, cancellationToken: ct).FirstOrDefaultAsync(ct);

        if (result is null)
            return new BrowseEdgeLabelsResult { Page = page, PageSize = pageSize };

        var totalArr = result["total"].AsBsonArray;
        var total = totalArr.Count > 0 ? totalArr[0]["n"].ToInt64() : 0;

        // fromTypes/toTypes are now pre-aggregated {type, count} docs from the two-stage group
        static List<TypeCount> TopTypes(BsonValue arr) =>
            arr
                .AsBsonArray.Where(x => x.IsBsonDocument && !x["type"].IsBsonNull && x["type"].AsString != "")
                .GroupBy(x => x["type"].AsString)
                .Select(g => new TypeCount { Type = g.Key, Count = g.Sum(x => x["count"].ToInt64()) })
                .OrderByDescending(t => t.Count)
                .Take(5)
                .ToList();

        var items = result["items"]
            .AsBsonArray.Select(d =>
            {
                var doc = d.AsBsonDocument;
                var sampleVal = doc.GetValue("sample", BsonNull.Value);
                EdgeSample? sample = sampleVal.IsBsonDocument
                    ? new EdgeSample
                    {
                        FromId = sampleVal["fromId"].ToInt32(),
                        FromName = sampleVal.AsBsonDocument.GetValue("fromName", "").AsString,
                        ToId = sampleVal["toId"].ToInt32(),
                        ToName = sampleVal.AsBsonDocument.GetValue("toName", "").AsString,
                    }
                    : null;

                return new EdgeLabelStatsDto
                {
                    Label = doc[MongoFields.Id].AsString,
                    Count = doc["count"].ToInt64(),
                    CanonCount = doc["canonCount"].ToInt64(),
                    LegendsCount = doc["legendsCount"].ToInt64(),
                    AvgWeight = doc.GetValue("avgWeight", 0.0).IsBsonNull ? 0.0 : doc["avgWeight"].ToDouble(),
                    TopFromTypes = TopTypes(doc["fromTypes"]),
                    TopToTypes = TopTypes(doc["toTypes"]),
                    Sample = sample,
                };
            })
            .ToList();

        return new BrowseEdgeLabelsResult
        {
            Items = items,
            Total = total,
            Page = page,
            PageSize = pageSize,
        };
    }

    // ── Lineage traversal (direction-pure, hierarchy-shaped) ──

    /// <summary>
    /// Walk a hierarchy-shaped relationship (e.g. <c>apprentice_of</c>, <c>parent_of</c>, <c>successor_of</c>)
    /// from a root entity, returning the full chain ordered by depth. This is the canonical
    /// <c>$graphLookup</c> use case: a single-label, single-direction recursive traversal —
    /// cheaper, clearer, and depth-aware compared to the hop-by-hop BFS used by
    /// <see cref="QueryGraphAsync"/>, which is tuned for mixed-direction neighborhood exploration.
    /// </summary>
    /// <param name="rootId">PageId of the starting entity.</param>
    /// <param name="label">The relationship label to follow (must exist as a forward label on <c>kg.edges</c>).</param>
    /// <param name="direction">
    ///   <c>forward</c>: walk edges in the stored direction (root is the <c>fromId</c> of the first hop,
    ///   next seed is each edge's <c>toId</c>). For <c>apprentice_of</c>, this walks towards masters.
    ///   For <c>parent_of</c>, it walks towards children.
    ///   <c>reverse</c>: walk against the stored direction (root is the <c>toId</c> of the first hop,
    ///   next seed is each edge's <c>fromId</c>). For <c>apprentice_of</c>, this walks towards apprentices.
    ///   For <c>parent_of</c>, it walks towards parents.
    /// </param>
    /// <param name="maxDepth">Max hops from root (1-10).</param>
    /// <param name="continuity">Optional continuity filter pushed into <c>restrictSearchWithMatch</c>.</param>
    public async Task<LineageResult> GetLineageAsync(int rootId, string label, string direction, int maxDepth, string? continuity, CancellationToken ct = default)
    {
        maxDepth = Math.Clamp(maxDepth, 1, 10);
        var reverse = string.Equals(direction, "reverse", StringComparison.OrdinalIgnoreCase);
        var normalizedDirection = reverse ? "reverse" : "forward";

        // Root must exist in kg.nodes — we start the aggregation there so $graphLookup
        // can seed from the node's own _id (which equals PageId on GraphNode).
        var rootNode = await _nodes.Find(n => n.PageId == rootId).FirstOrDefaultAsync(ct);
        if (rootNode is null)
            return new LineageResult
            {
                RootId = rootId,
                RootName = $"#{rootId}",
                Label = label,
                Direction = normalizedDirection,
                Chain = [],
            };

        // restrictSearchWithMatch is applied to every recursive lookup step, so
        // label + continuity are pushed fully server-side. The initial $match
        // on the root doc bounds the aggregation to a single starting document.
        var restrict = new BsonDocument { { RelationshipEdgeBsonFields.Label, label } };
        if (continuity is not null && Enum.TryParse<Continuity>(continuity, true, out var cont))
            restrict[RelationshipEdgeBsonFields.Continuity] = cont.ToString();

        // forward:  first hop matches edges where fromId = root, then seeds next hop with toId
        //           → connectToField="fromId" (match the current seed against edge.fromId)
        //           → connectFromField="toId" (next seed is edge.toId)
        // reverse:  first hop matches edges where toId = root, then seeds next hop with fromId
        //           → connectToField="toId"
        //           → connectFromField="fromId"
        var connectFrom = reverse ? RelationshipEdgeBsonFields.FromId : RelationshipEdgeBsonFields.ToId;
        var connectTo = reverse ? RelationshipEdgeBsonFields.ToId : RelationshipEdgeBsonFields.FromId;

        var pipeline = new BsonDocument[]
        {
            new("$match", new BsonDocument(MongoFields.Id, rootId)),
            new(
                "$graphLookup",
                new BsonDocument
                {
                    { "from", Collections.KgEdges },
                    { "startWith", "$" + MongoFields.Id },
                    { "connectFromField", connectFrom },
                    { "connectToField", connectTo },
                    { "as", "lineage" },
                    // $graphLookup depth is 0-based, so N hops requires maxDepth = N - 1
                    { "maxDepth", maxDepth - 1 },
                    { "depthField", "d" },
                    { "restrictSearchWithMatch", restrict },
                }
            ),
            new("$project", new BsonDocument { { MongoFields.Id, 1 }, { "lineage", 1 } }),
        };

        var nodesRaw = _nodes.Database.GetCollection<BsonDocument>(Collections.KgNodes);
        var result = await nodesRaw.Aggregate<BsonDocument>(pipeline, cancellationToken: ct).FirstOrDefaultAsync(ct);
        if (result is null || !result.Contains("lineage"))
            return new LineageResult
            {
                RootId = rootId,
                RootName = rootNode.Name,
                Label = label,
                Direction = normalizedDirection,
                Chain = [],
            };

        var chain = new List<LineageStep>();
        foreach (var e in result["lineage"].AsBsonArray.OfType<BsonDocument>())
        {
            var depth = e.Contains("d") ? e["d"].ToInt32() : 0;
            var fromId = e[RelationshipEdgeBsonFields.FromId].AsInt32;
            var toId = e[RelationshipEdgeBsonFields.ToId].AsInt32;
            chain.Add(
                new LineageStep
                {
                    // Depth is 0-based from $graphLookup's perspective (the first hop from
                    // root is depth 0). Surface as 1-based hops from root for the caller.
                    Hop = depth + 1,
                    FromId = fromId,
                    FromName = e[RelationshipEdgeBsonFields.FromName].AsString,
                    FromType = e[RelationshipEdgeBsonFields.FromType].AsString,
                    ToId = toId,
                    ToName = e[RelationshipEdgeBsonFields.ToName].AsString,
                    ToType = e[RelationshipEdgeBsonFields.ToType].AsString,
                    Label = e[RelationshipEdgeBsonFields.Label].AsString,
                    Evidence = e.Contains(RelationshipEdgeBsonFields.Evidence) ? e[RelationshipEdgeBsonFields.Evidence].AsString : string.Empty,
                }
            );
        }

        chain.Sort((a, b) => a.Hop.CompareTo(b.Hop));

        return new LineageResult
        {
            RootId = rootId,
            RootName = rootNode.Name,
            Label = label,
            Direction = normalizedDirection,
            Chain = chain,
        };
    }
}

public class LineageResult
{
    public int RootId { get; set; }
    public string RootName { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public List<LineageStep> Chain { get; set; } = [];
}

public class LineageStep
{
    public int Hop { get; set; }
    public int FromId { get; set; }
    public string FromName { get; set; } = string.Empty;
    public string FromType { get; set; } = string.Empty;
    public int ToId { get; set; }
    public string ToName { get; set; } = string.Empty;
    public string ToType { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Evidence { get; set; } = string.Empty;
}
