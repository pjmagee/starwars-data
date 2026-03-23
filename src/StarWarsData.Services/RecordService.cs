using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries;
using TimelineEvent = StarWarsData.Models.Entities.TimelineEvent;

namespace StarWarsData.Services;

public class RecordService
{
    readonly ILogger<RecordService> _logger;
    readonly InfoboxToEventsTransformer _recordToEventsTransformer;
    readonly SettingsOptions _settingsOptions;
    readonly IMongoClient _mongoClient;
    readonly string _pagesDbName;
    readonly string _timelineEventsDbName;
    readonly YearHelper _yearHelper;
    readonly IEmbeddingGenerator<string, Embedding<float>> _textEmbeddingGenerationService;

    public RecordService(
        ILogger<RecordService> logger,
        IOptions<SettingsOptions> settingsOptions,
        YearHelper yearHelper,
        IMongoClient mongoClient,
        IEmbeddingGenerator<string, Embedding<float>> textEmbeddingGenerationService,
        InfoboxToEventsTransformer recordToEventsTransformer
    )
    {
        _logger = logger;
        _settingsOptions = settingsOptions.Value;
        _mongoClient = mongoClient;
        _pagesDbName = _settingsOptions.PagesDb;
        _timelineEventsDbName = _settingsOptions.TimelineEventsDb;
        _yearHelper = yearHelper;
        _textEmbeddingGenerationService = textEmbeddingGenerationService;
        _recordToEventsTransformer = recordToEventsTransformer;
    }

    IMongoCollection<Page> PagesCollection =>
        _mongoClient.GetDatabase(_pagesDbName).GetCollection<Page>("Pages");

    /// <summary>
    /// Returns distinct sanitized template names from Pages that have infoboxes.
    /// </summary>
    public async Task<List<string>> GetCollectionNames(CancellationToken cancellationToken)
    {
        var pages = PagesCollection;
        var pipeline = new[]
        {
            new BsonDocument("$match", new BsonDocument("infobox", new BsonDocument("$ne", BsonNull.Value))),
            new BsonDocument("$group", new BsonDocument("_id", "$infobox.Template")),
        };
        var cursor = await pages.Aggregate<BsonDocument>(pipeline).ToListAsync(cancellationToken);
        var names = cursor
            .Select(d => d["_id"].IsBsonNull ? null : d["_id"].AsString)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => SanitizeTemplateName(n!))
            .Where(n => n != "Unknown")
            .Distinct()
            .OrderBy(x => x)
            .ToList();
        return names;
    }

    public async Task<Page?> GetPageById(int pageId, CancellationToken cancellationToken)
    {
        return await PagesCollection
            .Find(Builders<Page>.Filter.Eq(p => p.PageId, pageId))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<List<Page>> GetPagesByIds(IEnumerable<int> pageIds, CancellationToken cancellationToken)
    {
        var filter = Builders<Page>.Filter.In(p => p.PageId, pageIds);
        return await PagesCollection.Find(filter).ToListAsync(cancellationToken);
    }

    public async Task<List<string>> GetFilteredCollectionNames(
        Continuity? continuity,
        Universe? universe,
        CancellationToken cancellationToken)
    {
        var matchConditions = new BsonDocument("infobox", new BsonDocument("$ne", BsonNull.Value));

        if (continuity != null && continuity != Continuity.Both)
        {
            // Include pages that match the specific continuity OR are marked as Both
            matchConditions.Add("continuity", new BsonDocument("$in",
                new BsonArray { continuity.Value.ToString(), Continuity.Both.ToString() }));
        }

        if (universe == Universe.InUniverse)
        {
            // Exclude out-of-universe pages; include InUniverse and Unknown
            matchConditions.Add("universe", new BsonDocument("$ne", Universe.OutOfUniverse.ToString()));
        }
        else if (universe == Universe.OutOfUniverse)
        {
            // Include out-of-universe and Unknown
            matchConditions.Add("universe", new BsonDocument("$in",
                new BsonArray { Universe.OutOfUniverse.ToString(), Universe.Unknown.ToString() }));
        }

        var pipeline = new[]
        {
            new BsonDocument("$match", matchConditions),
            new BsonDocument("$group", new BsonDocument("_id", "$infobox.Template")),
        };

        var cursor = await PagesCollection.Aggregate<BsonDocument>(pipeline).ToListAsync(cancellationToken);
        return cursor
            .Select(d => d["_id"].IsBsonNull ? null : d["_id"].AsString)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => SanitizeTemplateName(n!))
            .Where(n => n != "Unknown")
            .Distinct()
            .OrderBy(x => x)
            .ToList();
    }

    public async Task DeletePagesCollections(CancellationToken cancellationToken)
    {
        var pagesDb = _mongoClient.GetDatabase(_pagesDbName);
        var collections = await pagesDb.ListCollectionNamesAsync(cancellationToken: cancellationToken);
        var collectionList = await collections.ToListAsync(cancellationToken);
        foreach (var collection in collectionList)
        {
            await pagesDb.DropCollectionAsync(collection, cancellationToken);
        }
    }

    public async Task DeleteTimelineCollections(CancellationToken cancellationToken)
    {
        var timelineDb = _mongoClient.GetDatabase(_timelineEventsDbName);
        var collections = await timelineDb.ListCollectionNamesAsync(cancellationToken: cancellationToken);
        var collectionList = await collections.ToListAsync(cancellationToken);
        foreach (var collection in collectionList)
        {
            await timelineDb.DropCollectionAsync(collection, cancellationToken);
        }
    }

    public async Task<PagedResult> GetSearchResult(
        string query,
        int page = 1,
        int pageSize = 50,
        CancellationToken token = default
    )
    {
        var pages = PagesCollection;

        // Use $text search on title + content, filtered to pages with infoboxes
        var textFilter = Builders<Page>.Filter.And(
            Builders<Page>.Filter.Ne(p => p.Infobox, null),
            Builders<Page>.Filter.Text(query)
        );

        var total = await pages.CountDocumentsAsync(textFilter, cancellationToken: token);
        var data = await pages
            .Find(textFilter)
            .Sort(Builders<Page>.Sort.MetaTextScore("score"))
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(token);

        // Fallback to regex if text search returns nothing
        if (total == 0)
        {
            var regexFilter = Builders<Page>.Filter.And(
                Builders<Page>.Filter.Ne(p => p.Infobox, null),
                Builders<Page>.Filter.Regex("title", new BsonRegularExpression(query, "i"))
            );
            total = await pages.CountDocumentsAsync(regexFilter, cancellationToken: token);
            data = await pages
                .Find(regexFilter)
                .Skip((page - 1) * pageSize)
                .Limit(pageSize)
                .ToListAsync(token);
        }

        return new PagedResult
        {
            Total = (int)total,
            Size = pageSize,
            Page = page,
            Items = data.Select(PageToInfobox).ToList(),
        };
    }

    public async Task<PagedResult> GetCollectionResult(
        string collectionName,
        string? searchText = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken token = default
    )
    {
        var pages = PagesCollection;

        // Match pages whose sanitized template matches the collection name
        var templateFilter = BuildTemplateFilter(collectionName);

        if (searchText is null)
        {
            var total = await pages.CountDocumentsAsync(templateFilter, cancellationToken: token);
            var data = await pages
                .Find(templateFilter)
                .Skip((page - 1) * pageSize)
                .Limit(pageSize)
                .ToListAsync(token);

            return new PagedResult
            {
                Total = (int)total,
                Size = pageSize,
                Page = page,
                Items = data.Select(PageToInfobox).ToList(),
            };
        }

        // Use $text search combined with template filter
        var textFilter = Builders<Page>.Filter.And(
            templateFilter,
            Builders<Page>.Filter.Text(searchText)
        );

        var textTotal = await pages.CountDocumentsAsync(textFilter, cancellationToken: token);
        var textData = await pages
            .Find(textFilter)
            .Sort(Builders<Page>.Sort.MetaTextScore("score"))
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(token);

        // Fallback to regex if text search returns nothing
        if (textTotal == 0)
        {
            var regexFilter = Builders<Page>.Filter.And(
                templateFilter,
                Builders<Page>.Filter.Regex("title", new BsonRegularExpression(searchText, "i"))
            );
            textTotal = await pages.CountDocumentsAsync(regexFilter, cancellationToken: token);
            textData = await pages
                .Find(regexFilter)
                .Skip((page - 1) * pageSize)
                .Limit(pageSize)
                .ToListAsync(token);
        }

        return new PagedResult
        {
            Total = (int)textTotal,
            Size = pageSize,
            Page = page,
            Items = textData.Select(PageToInfobox).ToList(),
        };
    }

    public async Task CreateCategorizedTimelineEvents(CancellationToken token)
    {
        var timelineEventsDb = _mongoClient.GetDatabase(_timelineEventsDbName);
        var pages = PagesCollection;

        // Get all distinct templates
        var templateNames = await GetCollectionNames(token);

        const int batchSize = 1000;
        var parallelOptions = new ParallelOptions { CancellationToken = token };

        await Parallel.ForEachAsync(
            templateNames,
            parallelOptions,
            async (templateName, ct) =>
            {
                try
                {
                    _logger.LogInformation(
                        "Processing timeline events for template: {TemplateName}",
                        templateName
                    );

                    var timelineCollection = timelineEventsDb.GetCollection<TimelineEvent>(templateName);

                    // Clear existing data
                    await timelineCollection.DeleteManyAsync(
                        FilterDefinition<TimelineEvent>.Empty,
                        ct
                    );

                    // Create indexes
                    var indexKeysDefinition = Builders<TimelineEvent>
                        .IndexKeys.Ascending(x => x.Demarcation)
                        .Ascending(x => x.Year);
                    await timelineCollection.Indexes.CreateOneAsync(
                        new CreateIndexModel<TimelineEvent>(indexKeysDefinition),
                        cancellationToken: ct
                    );

                    var templateIndexKeysDefinition = Builders<TimelineEvent>.IndexKeys.Ascending(
                        x => x.TemplateUri
                    );
                    await timelineCollection.Indexes.CreateOneAsync(
                        new CreateIndexModel<TimelineEvent>(templateIndexKeysDefinition),
                        cancellationToken: ct
                    );

                    var cleanedTemplateIndexKeysDefinition =
                        Builders<TimelineEvent>.IndexKeys.Ascending(x => x.Template);
                    await timelineCollection.Indexes.CreateOneAsync(
                        new CreateIndexModel<TimelineEvent>(cleanedTemplateIndexKeysDefinition),
                        cancellationToken: ct
                    );

                    // Query pages matching this template
                    var templateFilter = BuildTemplateFilter(templateName);
                    var cursor = pages.Find(templateFilter).ToCursor(ct);

                    var batch = new List<TimelineEvent>(batchSize);
                    var totalAdded = 0;

                    while (await cursor.MoveNextAsync(ct))
                    {
                        foreach (var page in cursor.Current)
                        {
                            var timelineEvents = _recordToEventsTransformer.Transform(page);
                            batch.AddRange(timelineEvents);

                            if (batch.Count >= batchSize)
                            {
                                if (batch.Count > 0)
                                {
                                    await timelineCollection.InsertManyAsync(
                                        batch,
                                        cancellationToken: ct
                                    );
                                    totalAdded += batch.Count;
                                    batch.Clear();
                                }
                            }
                        }
                    }

                    if (batch.Count > 0)
                    {
                        await timelineCollection.InsertManyAsync(batch, cancellationToken: ct);
                        totalAdded += batch.Count;
                    }

                    if (totalAdded == 0)
                    {
                        _logger.LogInformation(
                            "No timeline events generated for template: {TemplateName}",
                            templateName
                        );
                    }
                    else
                    {
                        _logger.LogInformation(
                            "Added {Count} timeline events for template: {TemplateName}",
                            totalAdded,
                            templateName
                        );
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Error processing timeline events for template: {TemplateName}",
                        templateName
                    );
                }
            }
        );

        _logger.LogInformation("Categorized timeline events creation complete");
    }

    /// <summary>
    /// Creates a MongoDB view per distinct infobox template type.
    /// Each view is a lightweight pipeline on the Pages collection filtered by infobox.Template.
    /// Drops any existing template views first.
    /// </summary>
    public async Task CreateTemplateViewsAsync(CancellationToken cancellationToken)
    {
        var database = _mongoClient.GetDatabase(_pagesDbName);

        // Drop existing views (only views, not real collections like Pages/JobState)
        var viewFilter = new BsonDocument("type", "view");
        using var viewCursor = await database.ListCollectionsAsync(
            new ListCollectionsOptions { Filter = viewFilter },
            cancellationToken);
        var existingViews = await viewCursor.ToListAsync(cancellationToken);

        foreach (var view in existingViews)
        {
            var viewName = view["name"].AsString;
            _logger.LogInformation("Dropping existing view: {ViewName}", viewName);
            await database.DropCollectionAsync(viewName, cancellationToken);
        }

        // Get distinct template names
        var templateNames = await GetCollectionNames(cancellationToken);

        foreach (var templateName in templateNames)
        {
            var pattern = templateName.Replace(" ", "[_ ]");
            var pipeline = new BsonArray
            {
                new BsonDocument("$match", new BsonDocument("infobox.Template",
                    new BsonDocument("$regex", new BsonRegularExpression($":{pattern}$", "i"))))
            };

            var command = new BsonDocument
            {
                { "create", templateName },
                { "viewOn", "Pages" },
                { "pipeline", pipeline }
            };

            try
            {
                await database.RunCommandAsync<BsonDocument>(command, cancellationToken: cancellationToken);
                _logger.LogInformation("Created view: {ViewName}", templateName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create view: {ViewName}", templateName);
            }
        }

        _logger.LogInformation("Template views creation complete. Created {Count} views.", templateNames.Count);
    }

    /// <summary>
    /// Creates indexes on the Pages collection and all timeline event collections
    /// to support common query patterns.
    /// </summary>
    public async Task EnsureIndexesAsync(CancellationToken cancellationToken)
    {
        var pagesDb = _mongoClient.GetDatabase(_pagesDbName);
        var pages = pagesDb.GetCollection<Page>("Pages");

        _logger.LogInformation("Creating indexes on Pages collection");

        // infobox.Template — template-based filtering (most common query pattern)
        await pages.Indexes.CreateOneAsync(
            new CreateIndexModel<Page>(
                Builders<Page>.IndexKeys.Ascending("infobox.Template"),
                new CreateIndexOptions { Name = "idx_infobox_template", Background = true }),
            cancellationToken: cancellationToken);

        // wikiUrl — relationship graph BFS lookups via $in
        await pages.Indexes.CreateOneAsync(
            new CreateIndexModel<Page>(
                Builders<Page>.IndexKeys.Ascending(p => p.WikiUrl),
                new CreateIndexOptions { Name = "idx_wikiUrl", Background = true }),
            cancellationToken: cancellationToken);

        // title — ascending index for exact/prefix lookups
        await pages.Indexes.CreateOneAsync(
            new CreateIndexModel<Page>(
                Builders<Page>.IndexKeys.Ascending(p => p.Title),
                new CreateIndexOptions { Name = "idx_title", Background = true }),
            cancellationToken: cancellationToken);

        // Full-text index on title (boosted) + content for $text queries
        await pages.Indexes.CreateOneAsync(
            new CreateIndexModel<Page>(
                Builders<Page>.IndexKeys
                    .Text(p => p.Title)
                    .Text(p => p.Content),
                new CreateIndexOptions
                {
                    Name = "idx_text_search",
                    Background = true,
                    Weights = new BsonDocument
                    {
                        { "title", 10 },
                        { "content", 1 }
                    }
                }),
            cancellationToken: cancellationToken);

        // continuity — global filtering
        await pages.Indexes.CreateOneAsync(
            new CreateIndexModel<Page>(
                Builders<Page>.IndexKeys.Ascending(p => p.Continuity),
                new CreateIndexOptions { Name = "idx_continuity", Background = true }),
            cancellationToken: cancellationToken);

        // infobox.Data.Label — AI toolkit label-based searches
        await pages.Indexes.CreateOneAsync(
            new CreateIndexModel<Page>(
                Builders<Page>.IndexKeys.Ascending("infobox.Data.Label"),
                new CreateIndexOptions { Name = "idx_infobox_data_label", Background = true }),
            cancellationToken: cancellationToken);

        // Compound: infobox.Template + continuity — filtered template queries
        await pages.Indexes.CreateOneAsync(
            new CreateIndexModel<Page>(
                Builders<Page>.IndexKeys.Ascending("infobox.Template").Ascending(p => p.Continuity),
                new CreateIndexOptions { Name = "idx_template_continuity", Background = true }),
            cancellationToken: cancellationToken);

        _logger.LogInformation("Pages indexes created");

        // Timeline event collections — add Continuity and Universe indexes
        var timelineDb = _mongoClient.GetDatabase(_timelineEventsDbName);
        var collectionNames = await timelineDb.ListCollectionNamesAsync(cancellationToken: cancellationToken);
        var collections = await collectionNames.ToListAsync(cancellationToken);

        foreach (var collName in collections)
        {
            var coll = timelineDb.GetCollection<TimelineEvent>(collName);

            await coll.Indexes.CreateOneAsync(
                new CreateIndexModel<TimelineEvent>(
                    Builders<TimelineEvent>.IndexKeys.Ascending(x => x.Continuity),
                    new CreateIndexOptions { Name = "idx_continuity", Background = true }),
                cancellationToken: cancellationToken);

            await coll.Indexes.CreateOneAsync(
                new CreateIndexModel<TimelineEvent>(
                    Builders<TimelineEvent>.IndexKeys.Ascending(x => x.Universe),
                    new CreateIndexOptions { Name = "idx_universe", Background = true }),
                cancellationToken: cancellationToken);

            await coll.Indexes.CreateOneAsync(
                new CreateIndexModel<TimelineEvent>(
                    Builders<TimelineEvent>.IndexKeys
                        .Ascending(x => x.Continuity)
                        .Ascending(x => x.Demarcation)
                        .Ascending(x => x.Year),
                    new CreateIndexOptions { Name = "idx_continuity_demarcation_year", Background = true }),
                cancellationToken: cancellationToken);

            _logger.LogInformation("Created indexes for timeline collection: {Collection}", collName);
        }

        // Character timelines collection indexes
        var charTimelineDb = _mongoClient.GetDatabase(_settingsOptions.CharacterTimelinesDb);
        var timelinesCollNames = await charTimelineDb.ListCollectionNamesAsync(cancellationToken: cancellationToken);
        var timelineColls = await timelinesCollNames.ToListAsync(cancellationToken);

        if (timelineColls.Contains("Timelines"))
        {
            var timelines = charTimelineDb.GetCollection<CharacterTimeline>("Timelines");

            await timelines.Indexes.CreateOneAsync(
                new CreateIndexModel<CharacterTimeline>(
                    Builders<CharacterTimeline>.IndexKeys.Ascending(t => t.CharacterPageId),
                    new CreateIndexOptions { Name = "idx_characterPageId", Background = true, Unique = true }),
                cancellationToken: cancellationToken);

            await timelines.Indexes.CreateOneAsync(
                new CreateIndexModel<CharacterTimeline>(
                    Builders<CharacterTimeline>.IndexKeys.Ascending(t => t.CharacterTitle),
                    new CreateIndexOptions { Name = "idx_characterTitle", Background = true }),
                cancellationToken: cancellationToken);

            await timelines.Indexes.CreateOneAsync(
                new CreateIndexModel<CharacterTimeline>(
                    Builders<CharacterTimeline>.IndexKeys.Ascending(t => t.Continuity),
                    new CreateIndexOptions { Name = "idx_continuity", Background = true }),
                cancellationToken: cancellationToken);

            _logger.LogInformation("Created indexes for character timelines collection");
        }

        _logger.LogInformation("All indexes created successfully");
    }

    public async Task CreateVectorIndexesAsync(CancellationToken cancellationToken)
    {
        await CreateCosineVectorIndexAsync("Pages");
    }

    async Task CreateCosineVectorIndexAsync(
        string collectionName,
        string field = "embedding",
        string indexName = "vector_index",
        int dims = 3072
    )
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
                        { "numDimensions", dims },
                        { "similarity", "cosine" },
                    },
                }
            },
        };

        var model = new CreateSearchIndexModel(
            name: indexName,
            definition: vectorDef
        );

        var collection = _mongoClient
            .GetDatabase(_pagesDbName)
            .GetCollection<BsonDocument>(collectionName);
        await collection.SearchIndexes.CreateOneAsync(model);

        Console.WriteLine(
            $"Created cosine vector index for '{collection.CollectionNamespace.CollectionName}.{field}'"
        );
    }

    public async Task DeleteVectorIndexesAsync(CancellationToken cancellationToken)
    {
        var collection = _mongoClient.GetDatabase(_pagesDbName).GetCollection<BsonDocument>("Pages");
        await collection.SearchIndexes.DropOneAsync("vector_index", cancellationToken);
    }

    public async Task DeleteOpenAiEmbeddingsAsync(CancellationToken token)
    {
        var collection = _mongoClient.GetDatabase(_pagesDbName).GetCollection<Page>("Pages");
        var update = Builders<Page>.Update.Unset(
            new StringFieldDefinition<Page>("embedding")
        );
        await collection.UpdateManyAsync(
            FilterDefinition<Page>.Empty,
            update,
            cancellationToken: token
        );
        _logger.LogInformation("Embeddings removed from Pages collection");
    }

    public async Task<List<string>> GetTimelineCategories(
        CancellationToken cancellationToken = default
    )
    {
        var timelineEventsDb = _mongoClient.GetDatabase(_timelineEventsDbName);
        var names = await timelineEventsDb.ListCollectionNamesAsync(
            cancellationToken: cancellationToken
        );
        List<string> results = await names.ToListAsync(cancellationToken);
        return results.OrderBy(x => x).ToList();
    }

    public Task ProcessEmbeddingsAsync(CancellationToken none)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Build a filter that matches Pages whose infobox.Template, when sanitized, equals the given name.
    /// Uses regex to match the template URL pattern ending with the collection name.
    /// </summary>
    static FilterDefinition<Page> BuildTemplateFilter(string sanitizedName)
    {
        // Templates are stored as URLs like "https://starwars.fandom.com/wiki/Template:Character"
        // We match the end of the template string after the last colon
        return Builders<Page>.Filter.Regex(
            "infobox.Template",
            new BsonRegularExpression($":{sanitizedName.Replace(" ", "[_ ]")}$", "i")
        );
    }

    /// <summary>
    /// Extracts a clean template name from a template URL.
    /// e.g. "https://starwars.fandom.com/wiki/Template:Character" → "Character"
    /// </summary>
    public static string SanitizeTemplateName(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return "Unknown";

        var working = template;
        var wikiIdx = working.IndexOf("/wiki/", StringComparison.OrdinalIgnoreCase);
        if (wikiIdx >= 0)
        {
            working = working[(wikiIdx + 6)..];
        }

        var lastColon = working.LastIndexOf(':');
        if (lastColon >= 0 && lastColon < working.Length - 1)
        {
            working = working[(lastColon + 1)..];
        }

        working = working.Split('?', '#')[0];
        return string.IsNullOrWhiteSpace(working) ? "Unknown" : working.Trim();
    }

    /// <summary>
    /// Project a Page into an Infobox shape for backward compatibility with frontend APIs.
    /// </summary>
    static Infobox PageToInfobox(Page page)
    {
        return new Infobox
        {
            PageId = page.PageId,
            WikiUrl = page.WikiUrl,
            Template = page.Infobox?.Template ?? "Unknown",
            ImageUrl = page.Infobox?.ImageUrl,
            Data = page.Infobox?.Data ?? [],
            Continuity = page.Continuity,
            PageTitle = page.Title,
        };
    }
}
