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
    readonly SettingsOptions _settingsOptions;
    readonly IMongoClient _mongoClient;

    public RecordService(ILogger<RecordService> logger, IOptions<SettingsOptions> settingsOptions, IMongoClient mongoClient)
    {
        _logger = logger;
        _settingsOptions = settingsOptions.Value;
        _mongoClient = mongoClient;
    }

    IMongoCollection<Page> PagesCollection => _mongoClient.GetDatabase(_settingsOptions.DatabaseName).GetCollection<Page>(Collections.Pages);

    /// <summary>
    /// Returns distinct sanitized template names from Pages that have infoboxes.
    /// </summary>
    public async Task<List<string>> GetCollectionNames(CancellationToken cancellationToken)
    {
        var pages = PagesCollection;
        var pipeline = new[]
        {
            new BsonDocument("$match", new BsonDocument(PageBsonFields.Infobox, new BsonDocument("$ne", BsonNull.Value))),
            new BsonDocument("$group", new BsonDocument(MongoFields.Id, "$" + PageBsonFields.InfoboxTemplate)),
        };
        var cursor = await pages.Aggregate<BsonDocument>(pipeline).ToListAsync(cancellationToken);
        var names = cursor
            .Select(d => d[MongoFields.Id].IsBsonNull ? null : d[MongoFields.Id].AsString)
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
        return await PagesCollection.Find(Builders<Page>.Filter.Eq(p => p.PageId, pageId)).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<List<Page>> GetPagesByIds(IEnumerable<int> pageIds, CancellationToken cancellationToken)
    {
        var filter = Builders<Page>.Filter.In(p => p.PageId, pageIds);
        return await PagesCollection.Find(filter).ToListAsync(cancellationToken);
    }

    public async Task<List<string>> GetFilteredCollectionNames(Continuity? continuity, Realm? realm, CancellationToken cancellationToken)
    {
        var matchConditions = new BsonDocument(PageBsonFields.Infobox, new BsonDocument("$ne", BsonNull.Value));

        if (continuity != null && continuity != Continuity.Both)
        {
            // Include pages that match the specific continuity OR are marked as Both
            matchConditions.Add(PageBsonFields.Continuity, new BsonDocument("$in", new BsonArray { continuity.Value.ToString(), Continuity.Both.ToString() }));
        }

        if (realm == Realm.Starwars)
        {
            // Exclude real-world pages; include Starwars and Unknown
            matchConditions.Add(PageBsonFields.Realm, new BsonDocument("$ne", Realm.Real.ToString()));
        }
        else if (realm == Realm.Real)
        {
            // Include real-world and Unknown
            matchConditions.Add(PageBsonFields.Realm, new BsonDocument("$in", new BsonArray { Realm.Real.ToString(), Realm.Unknown.ToString() }));
        }

        var pipeline = new[] { new BsonDocument("$match", matchConditions), new BsonDocument("$group", new BsonDocument(MongoFields.Id, "$" + PageBsonFields.InfoboxTemplate)) };

        var cursor = await PagesCollection.Aggregate<BsonDocument>(pipeline).ToListAsync(cancellationToken);
        return cursor
            .Select(d => d[MongoFields.Id].IsBsonNull ? null : d[MongoFields.Id].AsString)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => SanitizeTemplateName(n!))
            .Where(n => n != "Unknown")
            .Distinct()
            .OrderBy(x => x)
            .ToList();
    }

    public async Task DeletePagesCollections(CancellationToken cancellationToken)
    {
        var pagesDb = _mongoClient.GetDatabase(_settingsOptions.DatabaseName);
        var collections = await pagesDb.ListCollectionNamesAsync(cancellationToken: cancellationToken);
        var collectionList = await collections.ToListAsync(cancellationToken);
        foreach (var collection in collectionList)
        {
            await pagesDb.DropCollectionAsync(collection, cancellationToken);
        }
    }

    public async Task DeleteTimelineCollections(CancellationToken cancellationToken)
    {
        var db = _mongoClient.GetDatabase(_settingsOptions.DatabaseName);
        var collections = await db.ListCollectionNamesAsync(cancellationToken: cancellationToken);
        var collectionList = (await collections.ToListAsync(cancellationToken)).Where(n => n.StartsWith(Collections.TimelinePrefix)).ToList();
        foreach (var collection in collectionList)
        {
            await db.DropCollectionAsync(collection, cancellationToken);
        }
    }

    public async Task<PagedResult> GetCollectionResult(
        string collectionName,
        string? searchText = null,
        int page = 1,
        int pageSize = 50,
        Continuity? continuity = null,
        Realm? realm = null,
        CancellationToken token = default
    )
    {
        var pages = PagesCollection;

        // Match pages whose sanitized template matches the collection name
        var templateFilter = BuildTemplateFilter(collectionName);

        // Apply global filters (continuity/realm) at the DB level on the Page documents.
        var globalFilter = BuildPageGlobalFilter(continuity, realm);
        var baseFilter = Builders<Page>.Filter.And(templateFilter, globalFilter);

        if (searchText is null)
        {
            var total = await pages.CountDocumentsAsync(baseFilter, cancellationToken: token);
            var data = await pages.Find(baseFilter).Skip((page - 1) * pageSize).Limit(pageSize).ToListAsync(token);

            return new PagedResult
            {
                Total = (int)total,
                Size = pageSize,
                Page = page,
                Items = data.Select(PageToInfobox).ToList(),
            };
        }

        // Use $text search combined with template + global filters
        var textFilter = Builders<Page>.Filter.And(baseFilter, Builders<Page>.Filter.Text(searchText));

        var textTotal = await pages.CountDocumentsAsync(textFilter, cancellationToken: token);
        var textData = await pages.Find(textFilter).Sort(Builders<Page>.Sort.MetaTextScore("score")).Skip((page - 1) * pageSize).Limit(pageSize).ToListAsync(token);

        // Fallback to regex if text search returns nothing
        if (textTotal == 0)
        {
            var regexFilter = Builders<Page>.Filter.And(baseFilter, Builders<Page>.Filter.Regex(PageBsonFields.Title, new BsonRegularExpression(searchText, "i")));
            textTotal = await pages.CountDocumentsAsync(regexFilter, cancellationToken: token);
            textData = await pages.Find(regexFilter).Skip((page - 1) * pageSize).Limit(pageSize).ToListAsync(token);
        }

        return new PagedResult
        {
            Total = (int)textTotal,
            Size = pageSize,
            Page = page,
            Items = textData.Select(PageToInfobox).ToList(),
        };
    }

    /// <summary>
    /// Builds a DB-level filter on <see cref="Page"/> for the app's global continuity /
    /// realm filters. Matches the selected value plus Both/Unknown so "shared" and
    /// unclassified documents remain visible.
    /// </summary>
    private static FilterDefinition<Page> BuildPageGlobalFilter(Continuity? continuity, Realm? realm)
    {
        var filters = new List<FilterDefinition<Page>>();

        if (continuity is not null && continuity != Continuity.Both)
        {
            filters.Add(Builders<Page>.Filter.In(p => p.Continuity, [continuity.Value, Continuity.Both, Continuity.Unknown]));
        }

        if (realm is not null)
        {
            filters.Add(Builders<Page>.Filter.In(p => p.Realm, [realm.Value, Realm.Unknown]));
        }

        return filters.Count > 0 ? Builders<Page>.Filter.And(filters) : Builders<Page>.Filter.Empty;
    }

    /// <summary>
    /// Creates a MongoDB view per distinct infobox template type.
    /// Each view is a lightweight pipeline on the Pages collection filtered by infobox.Template.
    /// Drops any existing template views first.
    /// </summary>
    public async Task CreateTemplateViewsAsync(CancellationToken cancellationToken)
    {
        var database = _mongoClient.GetDatabase(_settingsOptions.DatabaseName);

        // Drop existing views (only views, not real collections like Pages/JobState)
        var viewFilter = new BsonDocument("type", "view");
        using var viewCursor = await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = viewFilter }, cancellationToken);
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
            var pipeline = new BsonArray { new BsonDocument("$match", new BsonDocument("infobox.Template", $"{Collections.TemplateUrlPrefix}{templateName}")) };

            var command = new BsonDocument
            {
                { "create", templateName },
                { "viewOn", Collections.Pages },
                { "pipeline", pipeline },
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
        var pagesDb = _mongoClient.GetDatabase(_settingsOptions.DatabaseName);
        var pages = pagesDb.GetCollection<Page>(Collections.Pages);

        _logger.LogInformation("Creating indexes on Pages collection");

        // infobox.Template — template-based filtering (most common query pattern)
        await pages.Indexes.CreateOneAsync(
            new CreateIndexModel<Page>(Builders<Page>.IndexKeys.Ascending("infobox.Template"), new CreateIndexOptions { Name = "idx_infobox_template", Background = true }),
            cancellationToken: cancellationToken
        );

        // wikiUrl — relationship graph BFS lookups via $in
        await pages.Indexes.CreateOneAsync(
            new CreateIndexModel<Page>(Builders<Page>.IndexKeys.Ascending(p => p.WikiUrl), new CreateIndexOptions { Name = "idx_wikiUrl", Background = true }),
            cancellationToken: cancellationToken
        );

        // title — ascending index for exact/prefix lookups
        await pages.Indexes.CreateOneAsync(
            new CreateIndexModel<Page>(Builders<Page>.IndexKeys.Ascending(p => p.Title), new CreateIndexOptions { Name = "idx_title", Background = true }),
            cancellationToken: cancellationToken
        );

        // Full-text index on title (boosted) + content for $text queries
        await pages.Indexes.CreateOneAsync(
            new CreateIndexModel<Page>(
                Builders<Page>.IndexKeys.Text(p => p.Title).Text(p => p.Content),
                new CreateIndexOptions
                {
                    Name = "idx_text_search",
                    Background = true,
                    Weights = new BsonDocument { { PageBsonFields.Title, 10 }, { PageBsonFields.Content, 1 } },
                }
            ),
            cancellationToken: cancellationToken
        );

        // continuity — global filtering
        await pages.Indexes.CreateOneAsync(
            new CreateIndexModel<Page>(Builders<Page>.IndexKeys.Ascending(p => p.Continuity), new CreateIndexOptions { Name = "idx_continuity", Background = true }),
            cancellationToken: cancellationToken
        );

        // infobox.Data.Label — AI toolkit label-based searches
        await pages.Indexes.CreateOneAsync(
            new CreateIndexModel<Page>(Builders<Page>.IndexKeys.Ascending("infobox.Data.Label"), new CreateIndexOptions { Name = "idx_infobox_data_label", Background = true }),
            cancellationToken: cancellationToken
        );

        // Compound: infobox.Template + continuity — filtered template queries
        await pages.Indexes.CreateOneAsync(
            new CreateIndexModel<Page>(
                Builders<Page>.IndexKeys.Ascending("infobox.Template").Ascending(p => p.Continuity),
                new CreateIndexOptions { Name = "idx_template_continuity", Background = true }
            ),
            cancellationToken: cancellationToken
        );

        _logger.LogInformation("Pages indexes created");

        // Timeline event collections — add Continuity and Realm indexes
        var timelineDb = _mongoClient.GetDatabase(_settingsOptions.DatabaseName);
        var collectionNames = await timelineDb.ListCollectionNamesAsync(cancellationToken: cancellationToken);
        var collections = (await collectionNames.ToListAsync(cancellationToken)).Where(n => n.StartsWith(Collections.TimelinePrefix)).ToList();

        foreach (var collName in collections)
        {
            var coll = timelineDb.GetCollection<TimelineEvent>(collName);

            await coll.Indexes.CreateOneAsync(
                new CreateIndexModel<TimelineEvent>(Builders<TimelineEvent>.IndexKeys.Ascending(x => x.Continuity), new CreateIndexOptions { Name = "idx_continuity", Background = true }),
                cancellationToken: cancellationToken
            );

            await coll.Indexes.CreateOneAsync(
                new CreateIndexModel<TimelineEvent>(Builders<TimelineEvent>.IndexKeys.Ascending(x => x.Realm), new CreateIndexOptions { Name = "idx_realm", Background = true }),
                cancellationToken: cancellationToken
            );

            await coll.Indexes.CreateOneAsync(
                new CreateIndexModel<TimelineEvent>(
                    Builders<TimelineEvent>.IndexKeys.Ascending(x => x.Continuity).Ascending(x => x.Demarcation).Ascending(x => x.Year),
                    new CreateIndexOptions { Name = "idx_continuity_demarcation_year", Background = true }
                ),
                cancellationToken: cancellationToken
            );

            _logger.LogInformation("Created indexes for timeline collection: {Collection}", collName);
        }

        // Character timelines collection indexes
        {
            var timelines = timelineDb.GetCollection<CharacterTimeline>(Collections.GenaiCharacterTimelines);

            await timelines.Indexes.CreateOneAsync(
                new CreateIndexModel<CharacterTimeline>(
                    Builders<CharacterTimeline>.IndexKeys.Ascending(t => t.CharacterPageId),
                    new CreateIndexOptions
                    {
                        Name = "idx_characterPageId",
                        Background = true,
                        Unique = true,
                    }
                ),
                cancellationToken: cancellationToken
            );

            await timelines.Indexes.CreateOneAsync(
                new CreateIndexModel<CharacterTimeline>(
                    Builders<CharacterTimeline>.IndexKeys.Ascending(t => t.CharacterTitle),
                    new CreateIndexOptions { Name = "idx_characterTitle", Background = true }
                ),
                cancellationToken: cancellationToken
            );

            await timelines.Indexes.CreateOneAsync(
                new CreateIndexModel<CharacterTimeline>(Builders<CharacterTimeline>.IndexKeys.Ascending(t => t.Continuity), new CreateIndexOptions { Name = "idx_continuity", Background = true }),
                cancellationToken: cancellationToken
            );

            _logger.LogInformation("Created indexes for character timelines collection");
        }

        _logger.LogInformation("All indexes created successfully");
    }

    public async Task<List<string>> GetTimelineCategories(CancellationToken cancellationToken = default)
    {
        var db = _mongoClient.GetDatabase(_settingsOptions.DatabaseName);
        var names = await db.ListCollectionNamesAsync(cancellationToken: cancellationToken);
        List<string> results = (await names.ToListAsync(cancellationToken))
            .Where(n => n.StartsWith(Collections.TimelinePrefix))
            .Select(n => n[Collections.TimelinePrefix.Length..])
            .OrderBy(x => x)
            .ToList();
        return results;
    }

    /// <summary>
    /// Build a filter that matches Pages whose infobox.Template equals the full template URL.
    /// </summary>
    static FilterDefinition<Page> BuildTemplateFilter(string sanitizedName)
    {
        return Builders<Page>.Filter.Eq("infobox.Template", $"{Collections.TemplateUrlPrefix}{sanitizedName}");
    }

    /// <summary>
    /// Extracts a clean template name from a template URL.
    /// e.g. "https://starwars.fandom.com/wiki/Template:Character" → "Character"
    /// </summary>
    public static string SanitizeTemplateName(string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
            return "Unknown";

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
