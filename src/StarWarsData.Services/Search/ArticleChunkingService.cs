using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries;

namespace StarWarsData.Services;

/// <summary>
/// Splits wiki article content into semantically meaningful chunks based on markdown headings,
/// generates embeddings via OpenAI, and stores the chunks for vector search retrieval.
/// </summary>
public partial class ArticleChunkingService
{
    readonly ILogger<ArticleChunkingService> _logger;
    readonly IMongoCollection<BsonDocument> _pages;
    readonly IMongoCollection<ArticleChunk> _chunks;
    readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    readonly OpenAiStatusService _aiStatus;

    /// <summary>Target max chars per chunk. Aims for ~1500 tokens of content — a good balance
    /// between semantic richness and staying within model limits. Token-dense content (tables,
    /// URLs) is handled by retry-with-truncation at embedding time.</summary>
    const int MaxChunkChars = 6000;

    /// <summary>Minimum chunk size to avoid tiny low-context fragments.</summary>
    const int MinChunkChars = 100;

    /// <summary>Overlap when sub-splitting large sections by paragraph.</summary>
    const int OverlapChars = 300;

    public ArticleChunkingService(
        ILogger<ArticleChunkingService> logger,
        IOptions<SettingsOptions> settings,
        IMongoClient mongoClient,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        OpenAiStatusService aiStatus
    )
    {
        _logger = logger;
        var db = mongoClient.GetDatabase(settings.Value.DatabaseName);
        _pages = db.GetCollection<BsonDocument>(Collections.Pages);
        _chunks = db.GetCollection<ArticleChunk>(Collections.SearchChunks);
        _embedder = embedder;
        _aiStatus = aiStatus;
    }

    /// <summary>
    /// Process all pages that haven't been chunked yet.
    /// </summary>
    public async Task ProcessAllAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Article chunking: starting full processing run");

        // Find pageIds that already have chunks
        var existingPageIds = await _chunks
            .Distinct(c => c.PageId, FilterDefinition<ArticleChunk>.Empty, cancellationToken: ct)
            .ToListAsync(ct);

        var existingSet = new HashSet<int>(existingPageIds);

        // Get all eligible pages that haven't been chunked
        var candidates = await _pages
            .Find(
                new BsonDocument
                {
                    { "content", new BsonDocument("$nin", new BsonArray { BsonNull.Value, "" }) },
                    { "infobox", new BsonDocument("$ne", BsonNull.Value) },
                    { "infobox.Template", new BsonDocument("$ne", BsonNull.Value) },
                }
            )
            .Project(
                new BsonDocument
                {
                    { "_id", 1 },
                    { "title", 1 },
                    { "wikiUrl", 1 },
                    { "content", 1 },
                    { "continuity", 1 },
                    { "universe", 1 },
                    { "infobox.Template", 1 },
                }
            )
            .ToListAsync(ct);

        var batch = candidates.Where(d => !existingSet.Contains(d["_id"].AsInt32)).ToList();

        _logger.LogInformation("Article chunking: {Count} pages to process", batch.Count);

        int totalChunks = 0;
        int processed = 0;
        int failed = 0;
        int consecutiveApiErrors = 0;

        foreach (var doc in batch)
        {
            if (ct.IsCancellationRequested)
                break;

            var pageId = doc["_id"].AsInt32;
            var title = doc.Contains("title") ? doc["title"].AsString : $"Page {pageId}";
            var wikiUrl =
                doc.Contains("wikiUrl") && !doc["wikiUrl"].IsBsonNull
                    ? doc["wikiUrl"].AsString
                    : "";

            try
            {
                var content = doc["content"].AsString;
                if (string.IsNullOrWhiteSpace(content))
                {
                    processed++;
                    continue;
                }

                var template =
                    doc.Contains("infobox") && doc["infobox"].AsBsonDocument.Contains("Template")
                        ? RecordService.SanitizeTemplateName(doc["infobox"]["Template"].AsString)
                        : "Unknown";

                var continuity =
                    doc.Contains("continuity") && !doc["continuity"].IsBsonNull
                        ? Enum.TryParse<Continuity>(doc["continuity"].AsString, true, out var c)
                            ? c
                            : Continuity.Unknown
                        : Continuity.Unknown;

                var universe =
                    doc.Contains("universe") && !doc["universe"].IsBsonNull
                        ? Enum.TryParse<Universe>(doc["universe"].AsString, true, out var u)
                            ? u
                            : Universe.Unknown
                        : Universe.Unknown;

                // Split content into chunks
                var sections = SplitByMarkdownHeadings(content);
                var chunks = new List<(string heading, string text)>();

                foreach (var (heading, text) in sections)
                {
                    var cleaned = StripBoilerplate(text);
                    if (string.IsNullOrWhiteSpace(cleaned) || cleaned.Length < MinChunkChars)
                        continue;

                    if (cleaned.Length <= MaxChunkChars)
                    {
                        chunks.Add((heading, cleaned));
                    }
                    else
                    {
                        // Sub-split large sections by paragraph with overlap
                        foreach (var subChunk in SplitByParagraph(cleaned))
                        {
                            chunks.Add((heading, subChunk));
                        }
                    }
                }

                if (chunks.Count == 0)
                {
                    processed++;
                    continue;
                }

                // Hard-truncate any oversized chunks before embedding
                for (int ci = 0; ci < chunks.Count; ci++)
                {
                    var (h, t) = chunks[ci];
                    if (t.Length > MaxChunkChars)
                        chunks[ci] = (h, t[..MaxChunkChars]);
                }

                // Prepare texts for embedding (prepend title + heading for context)
                var embeddingTexts = chunks
                    .Select(ch =>
                        string.IsNullOrWhiteSpace(ch.heading)
                            ? $"{title}: {ch.text}"
                            : $"{title} — {ch.heading}: {ch.text}"
                    )
                    .ToList();

                // Embed one at a time — retry with progressive truncation on token limit errors
                var allEmbeddings = new List<Embedding<float>>();
                for (int i = 0; i < embeddingTexts.Count; i++)
                {
                    var text = embeddingTexts[i];
                    Embedding<float>? embedding = null;

                    for (int attempt = 0; attempt < 3 && embedding is null; attempt++)
                    {
                        try
                        {
                            var result = await _embedder.GenerateAsync(
                                [text],
                                cancellationToken: ct
                            );
                            embedding = result[0];
                            _aiStatus.RecordSuccess("Embeddings");
                        }
                        catch (Exception ex)
                            when (ex.Message.Contains("maximum context length")
                                || ex.Message.Contains("maximum input length")
                            )
                        {
                            // Truncate by 40% each retry to get under the token limit
                            text = text[..(int)(text.Length * 0.6)];
                            _logger.LogWarning(
                                "Chunk {Index} for page {PageId} exceeded token limit, truncating to {Len} chars (attempt {Attempt})",
                                i,
                                pageId,
                                text.Length,
                                attempt + 1
                            );
                        }
                    }

                    if (embedding is null)
                    {
                        _logger.LogWarning(
                            "Skipping chunk {Index} for page {PageId} — could not embed after truncation",
                            i,
                            pageId
                        );
                        // Use zero vector as placeholder so chunk indexes stay aligned
                        allEmbeddings.Add(new Embedding<float>(new float[1536]));
                    }
                    else
                    {
                        allEmbeddings.Add(embedding);
                    }
                }

                // Build chunk documents, skipping any that failed to embed
                var chunkDocs = new List<ArticleChunk>();
                for (int i = 0; i < chunks.Count; i++)
                {
                    var vec = allEmbeddings[i].Vector.ToArray();
                    // Skip zero-vector placeholders (failed embeddings)
                    if (vec.All(v => v == 0f))
                        continue;

                    chunkDocs.Add(
                        new ArticleChunk
                        {
                            PageId = pageId,
                            Title = title,
                            WikiUrl = wikiUrl,
                            Heading = chunks[i].heading,
                            Section = chunks[i].heading.Replace(' ', '_'),
                            ChunkIndex = i,
                            Text = chunks[i].text,
                            Type = template,
                            Continuity = continuity,
                            Universe = universe,
                            Embedding = vec,
                        }
                    );
                }

                await _chunks.InsertManyAsync(chunkDocs, cancellationToken: ct);
                totalChunks += chunkDocs.Count;
                processed++;
                consecutiveApiErrors = 0; // Reset on success

                if (processed % 50 == 0)
                    _logger.LogInformation(
                        "Article chunking progress: {Processed}/{Total} pages, {Chunks} chunks created",
                        processed,
                        batch.Count,
                        totalChunks
                    );
            }
            catch (Exception ex) when (IsQuotaOrRateLimit(ex))
            {
                consecutiveApiErrors++;
                _aiStatus.RecordError("Embeddings", ex);
                _logger.LogWarning(
                    ex,
                    "API quota/rate limit hit on page {PageId} ({ConsecutiveErrors} consecutive). Stopping run to avoid wasting requests.",
                    pageId,
                    consecutiveApiErrors
                );
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to chunk page {PageId}: {Title}", pageId, title);
                failed++;
                if (IsApiError(ex))
                {
                    consecutiveApiErrors++;
                    if (consecutiveApiErrors >= 5)
                    {
                        _logger.LogWarning(
                            "Stopping chunking run after {Count} consecutive API errors",
                            consecutiveApiErrors
                        );
                        break;
                    }
                }
                else
                {
                    consecutiveApiErrors = 0;
                }
            }
        }

        _logger.LogInformation(
            "Article chunking complete: {Processed} pages processed, {Failed} failed, {Chunks} chunks created",
            processed,
            failed,
            totalChunks
        );
    }

    /// <summary>
    /// Get progress stats for the chunking dashboard.
    /// </summary>
    public async Task<ChunkingProgress> GetProgressAsync(CancellationToken ct = default)
    {
        // Total eligible pages (content + infobox)
        var eligibleFilter = new BsonDocument
        {
            { "content", new BsonDocument("$nin", new BsonArray { BsonNull.Value, "" }) },
            { "infobox", new BsonDocument("$ne", BsonNull.Value) },
            { "infobox.Template", new BsonDocument("$ne", BsonNull.Value) },
        };
        var totalEligible = (int)
            await _pages.CountDocumentsAsync(eligibleFilter, cancellationToken: ct);

        // Total chunks
        var totalChunks = await _chunks.CountDocumentsAsync(
            FilterDefinition<ArticleChunk>.Empty,
            cancellationToken: ct
        );

        // Distinct chunked pages
        var chunkedPageIds = await _chunks
            .Distinct(c => c.PageId, FilterDefinition<ArticleChunk>.Empty, cancellationToken: ct)
            .ToListAsync(ct);
        var chunkedPages = chunkedPageIds.Count;

        // Breakdown by type
        var typePipeline = new[]
        {
            new BsonDocument(
                "$group",
                new BsonDocument
                {
                    {
                        "_id",
                        new BsonDocument { { "type", "$type" }, { "pageId", "$pageId" } }
                    },
                    { "chunks", new BsonDocument("$sum", 1) },
                }
            ),
            new BsonDocument(
                "$group",
                new BsonDocument
                {
                    { "_id", "$_id.type" },
                    { "pages", new BsonDocument("$sum", 1) },
                    { "chunks", new BsonDocument("$sum", "$chunks") },
                }
            ),
            new BsonDocument("$sort", new BsonDocument("pages", -1)),
        };

        var chunksRaw = _chunks.Database.GetCollection<BsonDocument>(Collections.SearchChunks);
        var typeResults = await chunksRaw
            .Aggregate<BsonDocument>(typePipeline, cancellationToken: ct)
            .ToListAsync(ct);

        var byType = typeResults
            .Select(d =>
            {
                var pages = d["pages"].AsInt32;
                var chunks = d["chunks"].ToInt64();
                return new ChunkingTypeProgress
                {
                    Type = d["_id"].AsString,
                    Pages = pages,
                    Chunks = chunks,
                    AvgChunksPerPage = pages > 0 ? Math.Round((double)chunks / pages, 1) : 0,
                };
            })
            .ToList();

        // Throughput: count distinct pages chunked in the last hour
        var oneHourAgo = DateTime.UtcNow.AddHours(-1);
        var recentChunkedPages = await _chunks
            .Distinct(
                c => c.PageId,
                Builders<ArticleChunk>.Filter.Gte(c => c.CreatedAt, oneHourAgo),
                cancellationToken: ct
            )
            .ToListAsync(ct);
        var pagesPerHour = (double)recentChunkedPages.Count;
        var pendingPages = totalEligible - chunkedPages;
        double? estimatedHoursRemaining =
            pagesPerHour > 0 ? Math.Round(pendingPages / pagesPerHour, 1) : null;

        return new ChunkingProgress
        {
            TotalEligiblePages = totalEligible,
            ChunkedPages = chunkedPages,
            PendingPages = pendingPages,
            TotalChunks = totalChunks,
            AvgChunksPerPage =
                chunkedPages > 0 ? Math.Round((double)totalChunks / chunkedPages, 1) : 0,
            PagesPerHour = pagesPerHour,
            EstimatedHoursRemaining = estimatedHoursRemaining,
            ByType = byType,
        };
    }

    /// <summary>
    /// Split markdown content by heading boundaries (##, ###, ####).
    /// Returns (heading, sectionText) tuples.
    /// </summary>
    internal static List<(string heading, string text)> SplitByMarkdownHeadings(string content)
    {
        var sections = new List<(string heading, string text)>();

        // Split on markdown headings (## , ### , #### )
        var parts = HeadingSplitRegex().Split(content);

        // First part is content before any heading (intro)
        if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]))
            sections.Add(("Introduction", parts[0].Trim()));

        // Remaining parts alternate: heading, content, heading, content...
        // The regex captures the heading text as a group
        var matches = HeadingMatchRegex().Matches(content);
        var headingPositions = new List<(int index, string heading)>();

        foreach (Match m in matches)
            headingPositions.Add((m.Index, m.Groups[1].Value.Trim()));

        for (int i = 0; i < headingPositions.Count; i++)
        {
            var heading = headingPositions[i].heading;
            var start = headingPositions[i].index;

            // Find the content between this heading and the next (or end)
            var headingLine = content.IndexOf('\n', start);
            if (headingLine == -1)
                headingLine = start;
            else
                headingLine++;

            var end =
                i + 1 < headingPositions.Count ? headingPositions[i + 1].index : content.Length;

            var sectionText = content[headingLine..end].Trim();
            if (!string.IsNullOrWhiteSpace(sectionText))
                sections.Add((heading, sectionText));
        }

        // If no headings found, return entire content as single section
        if (sections.Count == 0 && !string.IsNullOrWhiteSpace(content))
            sections.Add(("", content.Trim()));

        return sections;
    }

    /// <summary>
    /// Sub-split a large section by paragraph boundaries with overlap.
    /// </summary>
    internal static List<string> SplitByParagraph(string text)
    {
        var chunks = new List<string>();
        // Split on double newlines (paragraph boundaries)
        var paragraphs = text.Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries);

        var current = "";
        foreach (var para in paragraphs)
        {
            var trimmed = para.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            if (
                current.Length + trimmed.Length + 2 > MaxChunkChars
                && current.Length > MinChunkChars
            )
            {
                chunks.Add(current.Trim());
                // Keep overlap from end of previous chunk
                current =
                    current.Length > OverlapChars
                        ? current[^OverlapChars..] + "\n\n" + trimmed
                        : trimmed;
            }
            else
            {
                current = string.IsNullOrEmpty(current) ? trimmed : current + "\n\n" + trimmed;
            }
        }

        if (current.Length >= MinChunkChars)
            chunks.Add(current.Trim());

        return chunks;
    }

    /// <summary>
    /// Strip wiki boilerplate: base64 images, badge links, table markup at the start of content.
    /// </summary>
    internal static string StripBoilerplate(string text)
    {
        // Remove markdown image links with base64 data URIs
        text = Base64ImageRegex().Replace(text, "");
        // Remove standalone base64 image references
        text = DataUriRegex().Replace(text, "");
        // Collapse excessive whitespace
        text = ExcessiveWhitespaceRegex().Replace(text, "\n\n");
        return text.Trim();
    }

    /// <summary>
    /// Ensure indexes exist on the chunks collection.
    /// </summary>
    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        await _chunks.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<ArticleChunk>(
                    Builders<ArticleChunk>.IndexKeys.Ascending(c => c.PageId),
                    new CreateIndexOptions { Name = "ix_pageId" }
                ),
                new CreateIndexModel<ArticleChunk>(
                    Builders<ArticleChunk>
                        .IndexKeys.Ascending(c => c.Type)
                        .Ascending(c => c.Continuity),
                    new CreateIndexOptions { Name = "ix_type_continuity" }
                ),
            ],
            ct
        );

        _logger.LogInformation("Article chunk indexes ensured");
    }

    /// <summary>
    /// Create a vector search index on the chunks collection.
    /// </summary>
    public async Task CreateVectorIndexAsync(CancellationToken ct = default)
    {
        var vectorDef = new BsonDocument
        {
            {
                "fields",
                new BsonArray
                {
                    new BsonDocument
                    {
                        { "type", "vector" },
                        { "path", "embedding" },
                        { "numDimensions", 1536 },
                        { "similarity", "cosine" },
                    },
                    new BsonDocument { { "type", "filter" }, { "path", "type" } },
                    new BsonDocument { { "type", "filter" }, { "path", "continuity" } },
                    new BsonDocument { { "type", "filter" }, { "path", "universe" } },
                }
            },
        };

        var model = new CreateSearchIndexModel(
            name: "chunks_vector_index",
            type: SearchIndexType.VectorSearch,
            definition: vectorDef
        );

        await _chunks.SearchIndexes.CreateOneAsync(model, ct);
        _logger.LogInformation("Vector search index created on chunks collection");
    }

    static bool IsQuotaOrRateLimit(Exception ex) =>
        ex.Message.Contains("429", StringComparison.Ordinal)
        || ex.Message.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("rate_limit", StringComparison.OrdinalIgnoreCase)
        || (ex.InnerException != null && IsQuotaOrRateLimit(ex.InnerException));

    static bool IsApiError(Exception ex) =>
        ex.GetType().Name.Contains("ClientResult", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("HTTP", StringComparison.OrdinalIgnoreCase)
        || (ex.InnerException != null && IsApiError(ex.InnerException));

    [GeneratedRegex(@"^#{2,4}\s+", RegexOptions.Multiline)]
    private static partial Regex HeadingSplitRegex();

    [GeneratedRegex(@"^#{2,4}\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex HeadingMatchRegex();

    [GeneratedRegex(@"\[!\[[^\]]*\]\(data:image[^\)]*\)\]\([^\)]*\)")]
    private static partial Regex Base64ImageRegex();

    [GeneratedRegex(@"data:image/[^)\s]+")]
    private static partial Regex DataUriRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExcessiveWhitespaceRegex();
}
