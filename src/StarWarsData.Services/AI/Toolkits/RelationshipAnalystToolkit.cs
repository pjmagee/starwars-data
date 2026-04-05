using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services;

/// <summary>
/// AI tools for the relationship analyst agent. These tools let the LLM
/// read page content, discover existing labels, and store extracted edges
/// into the persistent relationship graph.
/// </summary>
public class RelationshipAnalystToolkit
{
    readonly IMongoCollection<BsonDocument> _pages;
    readonly IMongoCollection<RelationshipEdge> _edges;
    readonly IMongoCollection<RelationshipCrawlState> _crawlState;
    readonly IMongoCollection<RelationshipLabel> _labels;

    public RelationshipAnalystToolkit(IMongoClient mongoClient, string databaseName)
    {
        var db = mongoClient.GetDatabase(databaseName);
        _pages = db.GetCollection<BsonDocument>(Collections.Pages);
        _edges = db.GetCollection<RelationshipEdge>(Collections.KgEdges);
        _crawlState = db.GetCollection<RelationshipCrawlState>(Collections.KgCrawlState);
        _labels = db.GetCollection<RelationshipLabel>(Collections.KgLabels);
    }

    [Description(
        "Read the full content and infobox data for a page by its PageId. "
            + "Returns the page title, infobox type, infobox labels/values/links, article content (truncated to ~8000 chars), "
            + "and continuity. Use this to understand an entity before extracting relationships."
    )]
    public async Task<string> GetPageContent([Description("The integer PageId (_id) of the page to read")] int pageId)
    {
        var doc = await _pages.Find(new BsonDocument(MongoFields.Id, pageId)).FirstOrDefaultAsync();
        if (doc == null)
            return JsonSerializer.Serialize(new { error = "Page not found" });

        var infobox = doc.Contains(PageBsonFields.Infobox) && !doc[PageBsonFields.Infobox].IsBsonNull ? doc[PageBsonFields.Infobox].AsBsonDocument : null;

        var template =
            infobox != null && infobox.Contains(InfoboxBsonFields.Template) && !infobox[InfoboxBsonFields.Template].IsBsonNull
                ? RecordService.SanitizeTemplateName(infobox[InfoboxBsonFields.Template].AsString)
                : "Unknown";

        var data =
            infobox
                ?[InfoboxBsonFields.Data].AsBsonArray.OfType<BsonDocument>()
                .Select(d => new
                {
                    label = d[InfoboxBsonFields.Label].AsString,
                    values = d[InfoboxBsonFields.Values].AsBsonArray.Select(v => v.AsString).ToList(),
                    links = d.Contains(InfoboxBsonFields.Links)
                        ? d[InfoboxBsonFields.Links]
                            .AsBsonArray.OfType<BsonDocument>()
                            .Select(l => new
                            {
                                text = l.Contains(InfoboxBsonFields.Content) ? l[InfoboxBsonFields.Content].AsString : "",
                                href = l.Contains(InfoboxBsonFields.Href) ? l[InfoboxBsonFields.Href].AsString : "",
                            })
                            .ToList()
                        : [],
                })
                .ToList() ?? [];

        var content = doc.Contains("content") && !doc["content"].IsBsonNull ? doc["content"].AsString : "";

        // Truncate content to keep token usage reasonable
        if (content.Length > 8000)
            content = content[..8000] + "\n... [truncated]";

        return JsonSerializer.Serialize(
            new
            {
                pageId,
                title = doc.Contains(PageBsonFields.Title) ? doc[PageBsonFields.Title].AsString : "",
                type = template,
                continuity = doc.Contains(PageBsonFields.Continuity) ? doc[PageBsonFields.Continuity].AsString : "Unknown",
                imageUrl = infobox != null && infobox.Contains(InfoboxBsonFields.ImageUrl) && !infobox[InfoboxBsonFields.ImageUrl].IsBsonNull ? infobox[InfoboxBsonFields.ImageUrl].AsString : "",
                wikiUrl = doc.Contains(PageBsonFields.WikiUrl) ? doc[PageBsonFields.WikiUrl].AsString : "",
                infobox = data,
                content,
            }
        );
    }

    [Description(
        "Get pages that are linked from a specific page's infobox. "
            + "Returns PageId, name, and type for each linked entity. "
            + "Use this to understand what entities a page references before extracting relationships."
    )]
    public async Task<string> GetLinkedPages([Description("The integer PageId (_id) of the source page")] int pageId, [Description("Max linked pages to return, default 30")] int limit = 30)
    {
        var doc = await _pages.Find(new BsonDocument(MongoFields.Id, pageId)).FirstOrDefaultAsync();
        if (doc == null)
            return "[]";

        var infobox = doc.Contains(PageBsonFields.Infobox) && !doc[PageBsonFields.Infobox].IsBsonNull ? doc[PageBsonFields.Infobox].AsBsonDocument : null;
        if (infobox == null)
            return "[]";

        // Collect all unique hrefs from infobox links
        var hrefs = infobox[InfoboxBsonFields.Data]
            .AsBsonArray.OfType<BsonDocument>()
            .Where(d => d.Contains(InfoboxBsonFields.Links))
            .SelectMany(d => d[InfoboxBsonFields.Links].AsBsonArray.OfType<BsonDocument>())
            .Where(l => l.Contains(InfoboxBsonFields.Href) && !string.IsNullOrWhiteSpace(l[InfoboxBsonFields.Href].AsString))
            .Select(l => l[InfoboxBsonFields.Href].AsString)
            .Distinct()
            .ToList();

        if (hrefs.Count == 0)
            return "[]";

        // Resolve hrefs to pages
        var linkedPages = await _pages
            .Find(Builders<BsonDocument>.Filter.In(PageBsonFields.WikiUrl, hrefs))
            .Limit(limit)
            .Project(
                new BsonDocument
                {
                    { MongoFields.Id, 1 },
                    { PageBsonFields.Title, 1 },
                    { PageBsonFields.Continuity, 1 },
                    { PageBsonFields.InfoboxTemplate, 1 },
                    { PageBsonFields.InfoboxImageUrl, 1 },
                    {
                        PageBsonFields.InfoboxData,
                        new BsonDocument(
                            "$filter",
                            new BsonDocument
                            {
                                { "input", "$" + PageBsonFields.InfoboxData },
                                { "as", "d" },
                                { "cond", new BsonDocument("$eq", new BsonArray { "$$d.Label", "Titles" }) },
                            }
                        )
                    },
                }
            )
            .ToListAsync();

        var results = linkedPages
            .Select(d =>
            {
                var ib = d.Contains(PageBsonFields.Infobox) && !d[PageBsonFields.Infobox].IsBsonNull ? d[PageBsonFields.Infobox].AsBsonDocument : null;
                var template =
                    ib != null && ib.Contains(InfoboxBsonFields.Template) && !ib[InfoboxBsonFields.Template].IsBsonNull
                        ? RecordService.SanitizeTemplateName(ib[InfoboxBsonFields.Template].AsString)
                        : "Unknown";
                var dataVal = ib != null && ib.Contains(InfoboxBsonFields.Data) && !ib[InfoboxBsonFields.Data].IsBsonNull ? ib[InfoboxBsonFields.Data] as BsonArray : null;
                var name =
                    dataVal?.OfType<BsonDocument>().FirstOrDefault()?[InfoboxBsonFields.Values].AsBsonArray.FirstOrDefault()?.AsString
                    ?? (d.Contains(PageBsonFields.Title) ? d[PageBsonFields.Title].AsString : "");

                return new
                {
                    pageId = d[MongoFields.Id].AsInt32,
                    name,
                    type = template,
                    continuity = d.Contains(PageBsonFields.Continuity) ? d[PageBsonFields.Continuity].AsString : "Unknown",
                };
            })
            .ToList();

        return JsonSerializer.Serialize(results);
    }

    [Description(
        "Get all canonical relationship labels currently in the registry. "
            + "ALWAYS call this before extracting relationships to reuse existing labels. "
            + "Returns each label with its reverse, description, and usage count."
    )]
    public async Task<string> GetExistingLabels()
    {
        var labels = await _labels.Find(Builders<RelationshipLabel>.Filter.Empty).SortByDescending(l => l.UsageCount).ToListAsync();

        var results = labels.Select(l => new
        {
            label = l.Label,
            reverse = l.Reverse,
            description = l.Description,
            fromTypes = l.FromTypes,
            toTypes = l.ToTypes,
            usageCount = l.UsageCount,
        });

        return JsonSerializer.Serialize(results);
    }

    [Description(
        "Search for a similar label in the registry before creating a new one. "
            + "Returns labels that are textually similar to your proposed label. "
            + "If a match is found, use the existing label instead of creating a new one."
    )]
    public async Task<string> FindSimilarLabel([Description("The proposed label text to check for similarity, e.g. 'hires', 'employer_of'")] string proposedLabel)
    {
        // Normalize for comparison
        var normalized = proposedLabel.ToLowerInvariant().Replace(" ", "_").Replace("-", "_");

        var allLabels = await _labels.Find(Builders<RelationshipLabel>.Filter.Empty).ToListAsync();

        // Simple fuzzy matching: check substring containment, common stems, and Levenshtein-like similarity
        var matches = allLabels
            .Select(l => new
            {
                label = l.Label,
                reverse = l.Reverse,
                description = l.Description,
                usageCount = l.UsageCount,
                score = ComputeSimilarity(normalized, l.Label.ToLowerInvariant()),
            })
            .Where(m => m.score > 0.3)
            .OrderByDescending(m => m.score)
            .Take(5)
            .ToList();

        return JsonSerializer.Serialize(matches);
    }

    [Description("Get edges already stored for a specific entity. " + "Call this before extracting to avoid creating duplicate relationships.")]
    public async Task<string> GetEntityEdges([Description("The PageId of the entity to check")] int pageId, [Description("Max edges to return, default 50")] int limit = 50)
    {
        var edges = await _edges.Find(Builders<RelationshipEdge>.Filter.Eq(e => e.FromId, pageId)).SortByDescending(e => e.Weight).Limit(limit).ToListAsync();

        var results = edges.Select(e => new
        {
            toId = e.ToId,
            toName = e.ToName,
            toType = e.ToType,
            label = e.Label,
            weight = e.Weight,
            evidence = e.Evidence,
        });

        return JsonSerializer.Serialize(results);
    }

    [Description(
        "Store extracted relationship edges into the graph. "
            + "Each edge is stored as a forward+reverse pair. "
            + "New labels are automatically registered. Duplicate edges (same fromId+toId+label) are skipped. "
            + "Returns the number of edges actually inserted."
    )]
    public async Task<string> StoreEdges([Description("The PageId this extraction is sourced from")] int sourcePageId, [Description("Array of edge objects to store")] List<EdgeDto> edges)
    {
        if (edges.Count == 0)
            return JsonSerializer.Serialize(new { inserted = 0 });

        var edgeDtos = edges;

        int inserted = 0;

        foreach (var dto in edgeDtos)
        {
            // Skip self-references
            if (dto.FromId == dto.ToId)
                continue;

            var label = dto.Label.ToLowerInvariant().Replace(" ", "_");
            var reverseLabel = dto.ReverseLabel.ToLowerInvariant().Replace(" ", "_");

            // Check for existing duplicate
            var existsFilter = Builders<RelationshipEdge>.Filter.And(
                Builders<RelationshipEdge>.Filter.Eq(e => e.FromId, dto.FromId),
                Builders<RelationshipEdge>.Filter.Eq(e => e.ToId, dto.ToId),
                Builders<RelationshipEdge>.Filter.Eq(e => e.Label, label)
            );

            if (await _edges.Find(existsFilter).AnyAsync())
                continue;

            var pairId = ObjectId.GenerateNewId().ToString();
            var continuity = Enum.TryParse<Continuity>(dto.Continuity, true, out var c) ? c : Continuity.Unknown;
            var now = DateTime.UtcNow;

            // Forward edge
            var forward = new RelationshipEdge
            {
                Id = ObjectId.GenerateNewId().ToString(),
                FromId = dto.FromId,
                FromName = dto.FromName,
                FromType = dto.FromType,
                ToId = dto.ToId,
                ToName = dto.ToName,
                ToType = dto.ToType,
                Label = label,
                Weight = dto.Weight,
                Evidence = dto.Evidence,
                SourcePageId = sourcePageId,
                Continuity = continuity,
                PairId = pairId,
                CreatedAt = now,
            };

            // Reverse edge
            var reverse = new RelationshipEdge
            {
                Id = ObjectId.GenerateNewId().ToString(),
                FromId = dto.ToId,
                FromName = dto.ToName,
                FromType = dto.ToType,
                ToId = dto.FromId,
                ToName = dto.FromName,
                ToType = dto.FromType,
                Label = reverseLabel,
                Weight = dto.Weight,
                Evidence = dto.Evidence,
                SourcePageId = sourcePageId,
                Continuity = continuity,
                PairId = pairId,
                CreatedAt = now,
            };

            try
            {
                await _edges.InsertManyAsync([forward, reverse], new InsertManyOptions { IsOrdered = false });
                inserted++;
            }
            catch (MongoBulkWriteException)
            {
                // Duplicate key on the reverse edge is expected when both sides
                // of a relationship have already been processed. Skip silently.
            }

            // Register or update labels
            await UpsertLabel(label, reverseLabel, dto.FromType, dto.ToType, dto.Evidence);
            await UpsertLabel(reverseLabel, label, dto.ToType, dto.FromType, dto.Evidence);
        }

        return JsonSerializer.Serialize(new { inserted, total = edgeDtos.Count });
    }

    [Description("Mark a page as processed in the crawl state tracker. " + "Call this after successfully extracting relationships from a page.")]
    public async Task<string> MarkProcessed(
        [Description("The PageId that was processed")] int pageId,
        [Description("Number of edges extracted")] int edgesExtracted,
        [Description("The entity name")] string name,
        [Description("The infobox type")] string type,
        [Description("Continuity: Canon, Legends, or Unknown")] string continuity = "Unknown"
    )
    {
        var cont = Enum.TryParse<Continuity>(continuity, true, out var c) ? c : Continuity.Unknown;

        await _crawlState.ReplaceOneAsync(
            Builders<RelationshipCrawlState>.Filter.Eq(s => s.PageId, pageId),
            new RelationshipCrawlState
            {
                PageId = pageId,
                Name = name,
                Type = type,
                Continuity = cont,
                Status = CrawlStatus.Completed,
                EdgesExtracted = edgesExtracted,
                ProcessedAt = DateTime.UtcNow,
                Version = 1,
            },
            new ReplaceOptions { IsUpsert = true }
        );

        return JsonSerializer.Serialize(new { status = "completed", pageId });
    }

    [Description("Mark a page as skipped (no meaningful relationships to extract). " + "Use for redirects, stubs, disambiguation pages, or pages with no infobox.")]
    public async Task<string> SkipPage(
        [Description("The PageId to skip")] int pageId,
        [Description("Why this page was skipped")] string reason,
        [Description("The entity name")] string name = "",
        [Description("The infobox type")] string type = ""
    )
    {
        await _crawlState.ReplaceOneAsync(
            Builders<RelationshipCrawlState>.Filter.Eq(s => s.PageId, pageId),
            new RelationshipCrawlState
            {
                PageId = pageId,
                Name = name,
                Type = type,
                Status = CrawlStatus.Skipped,
                ProcessedAt = DateTime.UtcNow,
                Error = reason,
                Version = 1,
            },
            new ReplaceOptions { IsUpsert = true }
        );

        return JsonSerializer.Serialize(
            new
            {
                status = "skipped",
                pageId,
                reason,
            }
        );
    }

    async Task UpsertLabel(string label, string reverse, string fromType, string toType, string evidence)
    {
        var filter = Builders<RelationshipLabel>.Filter.Eq(l => l.Label, label);
        var update = Builders<RelationshipLabel>
            .Update.SetOnInsert(l => l.Label, label)
            .SetOnInsert(l => l.Reverse, reverse)
            .SetOnInsert(l => l.Description, $"Extracted from: {evidence[..Math.Min(evidence.Length, 100)]}")
            .SetOnInsert(l => l.CreatedAt, DateTime.UtcNow)
            .AddToSetEach(l => l.FromTypes, [fromType])
            .AddToSetEach(l => l.ToTypes, [toType])
            .Inc(l => l.UsageCount, 1);

        await _labels.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });
    }

    static double ComputeSimilarity(string a, string b)
    {
        if (a == b)
            return 1.0;
        if (a.Contains(b) || b.Contains(a))
            return 0.8;

        // Check for common stem (e.g. "employ" in "employs" and "employed_by")
        var stemA = a.TrimEnd('s').Replace("_of", "").Replace("_by", "");
        var stemB = b.TrimEnd('s').Replace("_of", "").Replace("_by", "");
        if (stemA == stemB)
            return 0.7;
        if (stemA.Contains(stemB) || stemB.Contains(stemA))
            return 0.5;

        // Simple character-level Jaccard similarity
        var setA = new HashSet<char>(a);
        var setB = new HashSet<char>(b);
        var intersection = setA.Intersect(setB).Count();
        var union = setA.Union(setB).Count();
        return union == 0 ? 0 : (double)intersection / union * 0.4;
    }

    public IReadOnlyList<AITool> AsAIFunctions() =>
        [
            AIFunctionFactory.Create(GetPageContent, "get_page_content"),
            AIFunctionFactory.Create(GetLinkedPages, "get_linked_pages"),
            AIFunctionFactory.Create(GetExistingLabels, "get_existing_labels"),
            AIFunctionFactory.Create(FindSimilarLabel, "find_similar_label"),
            AIFunctionFactory.Create(GetEntityEdges, "get_entity_edges"),
            AIFunctionFactory.Create(StoreEdges, "store_edges"),
            AIFunctionFactory.Create(MarkProcessed, "mark_processed"),
            AIFunctionFactory.Create(SkipPage, "skip_page"),
        ];
}
