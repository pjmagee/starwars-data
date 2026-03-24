using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries;

namespace StarWarsData.Services;

/// <summary>
/// Orchestrates the relationship graph building process:
/// - Finds unprocessed pages and feeds them to the LLM analyst agent
/// - Provides dashboard progress stats
/// - Queries the persistent graph via $graphLookup
/// - Ensures indexes on the graph collections
/// </summary>
public class RelationshipGraphBuilderService
{
    readonly ILogger<RelationshipGraphBuilderService> _logger;
    readonly IMongoCollection<BsonDocument> _pages;
    readonly IMongoCollection<RelationshipEdge> _edges;
    readonly IMongoCollection<RelationshipCrawlState> _crawlState;
    readonly IMongoCollection<RelationshipLabel> _labels;
    readonly IChatClient _chatClient;
    readonly RelationshipAnalystToolkit _toolkit;
    readonly string _graphDb;

    public RelationshipGraphBuilderService(
        ILogger<RelationshipGraphBuilderService> logger,
        IOptions<SettingsOptions> settings,
        IMongoClient mongoClient,
        RelationshipAnalystToolkit toolkit,
        [FromKeyedServices("relationship-analyst")] IChatClient chatClient)
    {
        _logger = logger;
        _graphDb = settings.Value.RelationshipGraphDb;
        _pages = mongoClient.GetDatabase(settings.Value.PagesDb).GetCollection<BsonDocument>("Pages");
        var graphDatabase = mongoClient.GetDatabase(_graphDb);
        _edges = graphDatabase.GetCollection<RelationshipEdge>("edges");
        _crawlState = graphDatabase.GetCollection<RelationshipCrawlState>("crawl_state");
        _labels = graphDatabase.GetCollection<RelationshipLabel>("labels");
        _toolkit = toolkit;
        _chatClient = chatClient;
    }

    const string AnalystSystemPrompt = """
        You are a relationship analyst for a Star Wars knowledge graph stored in MongoDB.

        For each page you are given, your job is to extract ALL meaningful relationships between
        the subject entity and other entities mentioned in the article.

        PROCESS:
        1. Call get_existing_labels() to see the current label vocabulary
        2. Call get_page_content(pageId) to read the article
        3. Call get_linked_pages(pageId) to see what entities are referenced
        4. Call get_entity_edges(pageId) to see what's already stored (avoid duplicates)
        5. Extract relationships using ONLY existing labels where possible
        6. If a genuinely new relationship type is needed, call find_similar_label() first —
           if a close match exists, use that instead of creating a new one
        7. Call store_edges() with all extracted relationships
        8. Call mark_processed() when done, or skip_page() if the page has no meaningful relationships

        RULES:
        - ALWAYS prefer existing labels over inventing new ones
        - If unsure whether a label exists, call find_similar_label() before creating
        - Only create a new label when nothing in the registry fits
        - Skip pages with no meaningful relationships (redirects, stubs, disambiguation, list pages)
        - Skip pages with no infobox (they cannot be graph nodes)
        - Weight reflects confidence: explicit textual statements get 0.9+, inferred from context 0.6-0.8,
          inferred from infobox links alone 0.5
        - Evidence must be a brief quote or paraphrase from the article supporting the relationship
        - Extract relationships of ALL kinds: familial, political, military, economic, geographic,
          organizational, adversarial, master/apprentice, species/homeworld, etc.
        - Each entity in a relationship must have a valid PageId from get_linked_pages results.
          Do NOT invent PageIds. If an entity isn't in the linked pages results, skip that relationship.
        - Use the entity's infobox type (Character, Organization, CelestialBody, etc.) for fromType/toType
        """;

    /// <summary>
    /// Process a batch of unprocessed pages. Called by the Hangfire recurring job.
    /// </summary>
    public async Task ProcessBatchAsync(int batchSize = 100, CancellationToken ct = default)
    {
        _logger.LogInformation("Relationship graph builder: starting batch of {BatchSize}", batchSize);

        await EnsureIndexesAsync(ct);

        // Find pages that haven't been processed yet
        var processedIds = await _crawlState
            .Find(Builders<RelationshipCrawlState>.Filter.Empty)
            .Project(s => s.PageId)
            .ToListAsync(ct);

        var processedSet = new HashSet<int>(processedIds);

        // Get pages with infoboxes that haven't been processed, prioritizing high-link pages
        var pipeline = new[]
        {
            new BsonDocument("$match", new BsonDocument
            {
                { "infobox", new BsonDocument("$ne", BsonNull.Value) },
                { "infobox.Template", new BsonDocument("$ne", BsonNull.Value) },
            }),
            new BsonDocument("$addFields", new BsonDocument("linkCount",
                new BsonDocument("$sum",
                    new BsonDocument("$map", new BsonDocument
                    {
                        { "input", "$infobox.Data" },
                        { "as", "d" },
                        { "in", new BsonDocument("$size",
                            new BsonDocument("$ifNull", new BsonArray { "$$d.Links", new BsonArray() })) },
                    })))),
            new BsonDocument("$sort", new BsonDocument("linkCount", -1)),
            new BsonDocument("$limit", batchSize * 3), // Fetch extra to account for already-processed
            new BsonDocument("$project", new BsonDocument
            {
                { "_id", 1 },
                { "title", 1 },
                { "infobox.Template", 1 },
                { "linkCount", 1 },
            }),
        };

        var candidates = await _pages.Aggregate<BsonDocument>(pipeline, cancellationToken: ct).ToListAsync(ct);

        var batch = candidates
            .Where(d => !processedSet.Contains(d["_id"].AsInt32))
            .Take(batchSize)
            .ToList();

        _logger.LogInformation("Relationship graph builder: {Count} pages to process", batch.Count);

        int processed = 0;
        int failed = 0;

        foreach (var doc in batch)
        {
            if (ct.IsCancellationRequested) break;

            var pageId = doc["_id"].AsInt32;
            var title = doc.Contains("title") ? doc["title"].AsString : $"Page {pageId}";

            try
            {
                _logger.LogInformation("Processing page {PageId}: {Title}", pageId, title);

                // Mark as processing
                await _crawlState.ReplaceOneAsync(
                    Builders<RelationshipCrawlState>.Filter.Eq(s => s.PageId, pageId),
                    new RelationshipCrawlState
                    {
                        PageId = pageId,
                        Name = title,
                        Status = CrawlStatus.Processing,
                    },
                    new ReplaceOptions { IsUpsert = true },
                    ct);

                var messages = new List<ChatMessage>
                {
                    new(ChatRole.System, AnalystSystemPrompt),
                    new(ChatRole.User, $"Process page with PageId={pageId}. Extract all relationships you can find."),
                };

                var options = new ChatOptions
                {
                    Tools = [.. _toolkit.AsAIFunctions()],
                    MaxOutputTokens = 4096,
                    ToolMode = ChatToolMode.Auto,
                };

                // The chat client is built with UseFunctionInvocation(), so it handles
                // the tool call loop automatically — a single GetResponseAsync call runs
                // the full agent conversation until the LLM stops calling tools.
                await _chatClient.GetResponseAsync(messages, options, ct);

                processed++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process page {PageId}: {Title}", pageId, title);
                failed++;

                await _crawlState.ReplaceOneAsync(
                    Builders<RelationshipCrawlState>.Filter.Eq(s => s.PageId, pageId),
                    new RelationshipCrawlState
                    {
                        PageId = pageId,
                        Name = title,
                        Status = CrawlStatus.Failed,
                        Error = ex.Message[..Math.Min(ex.Message.Length, 500)],
                        ProcessedAt = DateTime.UtcNow,
                    },
                    new ReplaceOptions { IsUpsert = true },
                    ct);
            }
        }

        _logger.LogInformation("Relationship graph builder: batch complete. Processed={Processed}, Failed={Failed}",
            processed, failed);
    }

    /// <summary>
    /// Get overall progress for the dashboard.
    /// </summary>
    public async Task<GraphBuilderProgress> GetProgressAsync(CancellationToken ct = default)
    {
        // Total pages with infoboxes
        var totalPages = (int)await _pages.CountDocumentsAsync(
            new BsonDocument("infobox", new BsonDocument("$ne", BsonNull.Value)), cancellationToken: ct);

        // Crawl state aggregation by status
        var statePipeline = new[]
        {
            new BsonDocument("$group", new BsonDocument
            {
                { "_id", new BsonDocument { { "status", "$status" }, { "type", "$type" } } },
                { "count", new BsonDocument("$sum", 1) },
            }),
        };

        var stateResults = await _crawlState
            .Aggregate<BsonDocument>(statePipeline, cancellationToken: ct)
            .ToListAsync(ct);

        int processedPages = 0, skippedPages = 0, failedPages = 0;
        var byType = new Dictionary<string, (int total, int processed, int skipped, int failed)>();

        foreach (var doc in stateResults)
        {
            var id = doc["_id"].AsBsonDocument;
            var status = id["status"].AsString;
            var type = id.Contains("type") && !id["type"].IsBsonNull ? id["type"].AsString : "Unknown";
            var count = doc["count"].AsInt32;

            if (!byType.ContainsKey(type))
                byType[type] = (0, 0, 0, 0);
            var current = byType[type];

            switch (status)
            {
                case "Completed":
                    processedPages += count;
                    byType[type] = (current.total + count, current.processed + count, current.skipped, current.failed);
                    break;
                case "Skipped":
                    skippedPages += count;
                    byType[type] = (current.total + count, current.processed, current.skipped + count, current.failed);
                    break;
                case "Failed":
                    failedPages += count;
                    byType[type] = (current.total + count, current.processed, current.skipped, current.failed + count);
                    break;
                default:
                    byType[type] = (current.total + count, current.processed, current.skipped, current.failed);
                    break;
            }
        }

        // Total edges
        var totalEdges = await _edges.CountDocumentsAsync(
            Builders<RelationshipEdge>.Filter.Empty, cancellationToken: ct);

        // Total labels
        var totalLabels = (int)await _labels.CountDocumentsAsync(
            Builders<RelationshipLabel>.Filter.Empty, cancellationToken: ct);

        // Recent labels
        var recentLabels = await _labels
            .Find(Builders<RelationshipLabel>.Filter.Empty)
            .SortByDescending(l => l.CreatedAt)
            .Limit(10)
            .ToListAsync(ct);

        return new GraphBuilderProgress
        {
            TotalPages = totalPages,
            ProcessedPages = processedPages,
            SkippedPages = skippedPages,
            FailedPages = failedPages,
            PendingPages = totalPages - processedPages - skippedPages - failedPages,
            TotalEdges = totalEdges / 2, // Each relationship has forward+reverse
            TotalLabels = totalLabels,
            ByType = byType.Select(kv => new TypeProgress
            {
                Type = kv.Key,
                Total = kv.Value.total,
                Processed = kv.Value.processed,
                Skipped = kv.Value.skipped,
                Failed = kv.Value.failed,
            }).OrderByDescending(t => t.Total).ToList(),
            RecentLabels = recentLabels.Select(l => new RecentLabel
            {
                Label = l.Label,
                Reverse = l.Reverse,
                Description = l.Description,
                UsageCount = l.UsageCount,
                CreatedAt = l.CreatedAt,
            }).ToList(),
        };
    }

    /// <summary>
    /// Query the relationship graph using $graphLookup.
    /// </summary>
    public async Task<RelationshipGraphResult> QueryGraphAsync(
        int rootId,
        IReadOnlyList<string> labels,
        int maxDepth = 2,
        Continuity? continuity = null,
        CancellationToken ct = default)
    {
        var edgesCollection = _edges.Database.GetCollection<BsonDocument>("edges");

        // Get root entity info
        var rootPage = await _pages.Find(new BsonDocument("_id", rootId)).FirstOrDefaultAsync(ct);
        var rootName = "";
        if (rootPage != null)
        {
            var ib = rootPage.Contains("infobox") && !rootPage["infobox"].IsBsonNull
                ? rootPage["infobox"].AsBsonDocument : null;
            rootName = ib?["Data"].AsBsonArray.OfType<BsonDocument>()
                .FirstOrDefault(d => d["Label"].AsString == "Titles")
                ?["Values"].AsBsonArray.FirstOrDefault()?.AsString
                ?? (rootPage.Contains("title") ? rootPage["title"].AsString : "");
        }

        var labelFilter = labels.Count > 0
            ? new BsonDocument("label", new BsonDocument("$in", new BsonArray(labels)))
            : new BsonDocument();

        var restrictMatch = new BsonDocument();
        if (labels.Count > 0)
            restrictMatch["label"] = new BsonDocument("$in", new BsonArray(labels));

        // Apply continuity filter to edges
        if (continuity.HasValue)
        {
            labelFilter["continuity"] = continuity.Value.ToString();
            restrictMatch["continuity"] = continuity.Value.ToString();
        }

        var pipeline = new[]
        {
            new BsonDocument("$match", new BsonDocument
            {
                { "fromId", rootId },
            }.AddRange(labelFilter)),
            new BsonDocument("$graphLookup", new BsonDocument
            {
                { "from", "edges" },
                { "startWith", "$toId" },
                { "connectFromField", "toId" },
                { "connectToField", "fromId" },
                { "as", "network" },
                { "maxDepth", maxDepth },
                { "restrictSearchWithMatch", restrictMatch },
            }),
        };

        var results = await edgesCollection
            .Aggregate<BsonDocument>(pipeline, cancellationToken: ct)
            .ToListAsync(ct);

        // Collect unique nodes and edges
        var nodes = new Dictionary<int, RelationshipGraphNode>();
        var edgeSet = new HashSet<(int from, int to, string label)>();

        // Add root
        if (rootPage != null)
        {
            var ib = rootPage.Contains("infobox") && !rootPage["infobox"].IsBsonNull
                ? rootPage["infobox"].AsBsonDocument : null;
            nodes[rootId] = new RelationshipGraphNode
            {
                Id = rootId,
                Name = rootName,
                Type = ib != null && ib.Contains("Template") && !ib["Template"].IsBsonNull
                    ? RecordService.SanitizeTemplateName(ib["Template"].AsString) : "Unknown",
                ImageUrl = ib != null && ib.Contains("ImageUrl") && !ib["ImageUrl"].IsBsonNull
                    ? ib["ImageUrl"].AsString : "",
            };
        }

        foreach (var doc in results)
        {
            // Direct edges from root
            var toId = doc["toId"].AsInt32;
            var toName = doc["toName"].AsString;
            var toType = doc["toType"].AsString;
            var label = doc["label"].AsString;
            var weight = doc.Contains("weight") ? doc["weight"].ToDouble() : 0.5;

            if (!nodes.ContainsKey(toId))
                nodes[toId] = new RelationshipGraphNode { Id = toId, Name = toName, Type = toType };

            edgeSet.Add((rootId, toId, label));

            // Network edges from $graphLookup
            if (doc.Contains("network"))
            {
                foreach (var netDoc in doc["network"].AsBsonArray.OfType<BsonDocument>())
                {
                    var nFromId = netDoc["fromId"].AsInt32;
                    var nToId = netDoc["toId"].AsInt32;
                    var nLabel = netDoc["label"].AsString;
                    var nWeight = netDoc.Contains("weight") ? netDoc["weight"].ToDouble() : 0.5;

                    if (!nodes.ContainsKey(nFromId))
                        nodes[nFromId] = new RelationshipGraphNode
                        {
                            Id = nFromId,
                            Name = netDoc["fromName"].AsString,
                            Type = netDoc["fromType"].AsString,
                        };

                    if (!nodes.ContainsKey(nToId))
                        nodes[nToId] = new RelationshipGraphNode
                        {
                            Id = nToId,
                            Name = netDoc["toName"].AsString,
                            Type = netDoc["toType"].AsString,
                        };

                    edgeSet.Add((nFromId, nToId, nLabel));
                }
            }
        }

        return new RelationshipGraphResult
        {
            RootId = rootId,
            RootName = rootName,
            Nodes = nodes.Values.ToList(),
            Edges = edgeSet.Select(e => new RelationshipGraphEdge
            {
                FromId = e.from,
                ToId = e.to,
                Label = e.label,
            }).ToList(),
        };
    }

    /// <summary>
    /// Get distinct labels stored for a specific entity (for query-time LLM to pick relevant ones).
    /// </summary>
    public async Task<List<string>> GetEntityLabelsAsync(int pageId, Continuity? continuity = null, CancellationToken ct = default)
    {
        var edgesCollection = _edges.Database.GetCollection<BsonDocument>("edges");
        var matchFilter = new BsonDocument("fromId", pageId);
        if (continuity.HasValue)
            matchFilter["continuity"] = continuity.Value.ToString();

        var pipeline = new[]
        {
            new BsonDocument("$match", matchFilter),
            new BsonDocument("$group", new BsonDocument("_id", "$label")),
            new BsonDocument("$sort", new BsonDocument("_id", 1)),
        };

        var results = await edgesCollection
            .Aggregate<BsonDocument>(pipeline, cancellationToken: ct)
            .ToListAsync(ct);

        return results.Select(d => d["_id"].AsString).ToList();
    }

    /// <summary>
    /// Ensure required indexes exist on graph collections.
    /// </summary>
    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        // edges: $graphLookup traversal
        await _edges.Indexes.CreateManyAsync([
            new CreateIndexModel<RelationshipEdge>(
                Builders<RelationshipEdge>.IndexKeys.Ascending(e => e.FromId),
                new CreateIndexOptions { Name = "ix_fromId" }),
            new CreateIndexModel<RelationshipEdge>(
                Builders<RelationshipEdge>.IndexKeys
                    .Ascending(e => e.FromId)
                    .Ascending(e => e.ToId)
                    .Ascending(e => e.Label),
                new CreateIndexOptions { Name = "ix_fromId_toId_label", Unique = true }),
            new CreateIndexModel<RelationshipEdge>(
                Builders<RelationshipEdge>.IndexKeys.Ascending(e => e.SourcePageId),
                new CreateIndexOptions { Name = "ix_sourcePageId" }),
            new CreateIndexModel<RelationshipEdge>(
                Builders<RelationshipEdge>.IndexKeys.Ascending(e => e.PairId),
                new CreateIndexOptions { Name = "ix_pairId" }),
        ], ct);

        // crawl_state: status + type queries
        await _crawlState.Indexes.CreateManyAsync([
            new CreateIndexModel<RelationshipCrawlState>(
                Builders<RelationshipCrawlState>.IndexKeys
                    .Ascending(s => s.Status)
                    .Ascending(s => s.Type),
                new CreateIndexOptions { Name = "ix_status_type" }),
        ], ct);

        _logger.LogInformation("Relationship graph indexes ensured");
    }
}
