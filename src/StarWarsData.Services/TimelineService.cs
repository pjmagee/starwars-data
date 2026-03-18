using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries;
using TimelineEvent = StarWarsData.Models.Entities.TimelineEvent;

namespace StarWarsData.Services;

public class TimelineService
{
    readonly ILogger<TimelineService> _logger;
    readonly TemplateHelper _templateHelper;
    readonly IMongoDatabase _timelineEventsDb;
    readonly IMongoClient _mongoClient;

    public TimelineService(
        ILogger<TimelineService> logger,
        IOptions<SettingsOptions> settingsOptions,
        IMongoClient mongoClient,
        TemplateHelper templateHelper
    )
    {
        _logger = logger;
        _templateHelper = templateHelper;
        _mongoClient = mongoClient;
        _timelineEventsDb = mongoClient.GetDatabase(settingsOptions.Value.TimelineEventsDb);
    }

    public async Task<GroupedTimelineResult> GetTimelineEvents(
        IList<string> templates,
        Continuity? continuity = null,
        Universe? universe = null,
        int page = 1,
        int pageSize = 20,
        float? yearFrom = null,
        Demarcation? yearFromDemarcation = null,
        float? yearTo = null,
        Demarcation? yearToDemarcation = null,
        string? search = null
    )
    {
        var allTimelineEvents = new List<TimelineEvent>();

        // Get all available collections from timeline-events db
        var availableCollections = await GetTimelineCategories();

        // If templates are specified, filter collections; otherwise use all
        var collectionsToQuery = templates.Any()
            ? availableCollections
                .Where(c => templates.Contains(c))
                .ToList()
            : availableCollections;

        // Build combined filter
        var filters = new List<FilterDefinition<TimelineEvent>>
        {
            BuildContinuityFilter(continuity),
            BuildUniverseFilter(universe),
        };

        if (yearFrom.HasValue && yearFromDemarcation.HasValue && yearTo.HasValue && yearToDemarcation.HasValue)
        {
            filters.Add(BuildYearRangeFilter(yearFrom.Value, yearFromDemarcation.Value, yearTo.Value, yearToDemarcation.Value));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            filters.Add(Builders<TimelineEvent>.Filter.Regex(
                x => x.Title,
                new BsonRegularExpression(search, "i")
            ));
        }

        var combinedFilter = Builders<TimelineEvent>.Filter.And(filters);

        // Query each relevant collection and combine results
        foreach (var collectionName in collectionsToQuery)
        {
            var collection = _timelineEventsDb.GetCollection<TimelineEvent>(collectionName);
            var events = await collection.Find(combinedFilter).ToListAsync();
            allTimelineEvents.AddRange(events);
        }

        // Sort all events
        allTimelineEvents.Sort();

        // Apply pagination
        var total = allTimelineEvents.Count;
        var pagedEvents = allTimelineEvents.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        // Group by year
        var groupedByYear = pagedEvents
            .GroupBy(x => x.DisplayYear)
            .Select(x => new GroupedTimelines { Events = x.ToList(), Year = x.Key });

        return new GroupedTimelineResult
        {
            Total = total,
            Size = pageSize,
            Page = page,
            Items = groupedByYear,
        };
    }

    public async Task<List<string>> GetDistinctTemplatesAsync()
    {
        // Get all collection names from timeline-events db, these represent the templates/categories
        var collections = await GetTimelineCategories();
        return collections
            .Select(_templateHelper.GetTemplateFromUri)
            .Distinct()
            .OrderBy(x => x)
            .ToList();
    }

    public async Task<GroupedTimelineResult> GetCategoryTimelineEvents(
        string category,
        Continuity? continuity = null,
        Universe? universe = null,
        int page = 1,
        int pageSize = 20
    )
    {
        var categoryCollection = _timelineEventsDb.GetCollection<TimelineEvent>(category);

        // Build combined filter
        var continuityFilter = BuildContinuityFilter(continuity);
        var universeFilter = BuildUniverseFilter(universe);
        var combinedFilter = Builders<TimelineEvent>.Filter.And(continuityFilter, universeFilter);

        var sort = Builders<TimelineEvent>
            .Sort.Ascending(x => x.Demarcation)
            .Ascending(x => x.Year);

        var timelineEventDocuments = await categoryCollection
            .Find(combinedFilter)
            .Sort(sort)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync();

        var timelineEvents = timelineEventDocuments
            .Select(doc => new TimelineEvent
            {
                Title = doc.Title,
                TemplateUri = doc.Template,
                ImageUrl = doc.ImageUrl,
                Demarcation = doc.Demarcation,
                Year = doc.Year,
                Properties = doc.Properties,
                DateEvent = doc.DateEvent,
                Continuity = doc.Continuity,
            })
            .ToList();

        timelineEvents.Sort();

        var groupedByYear = timelineEvents
            .GroupBy(x => x.DisplayYear)
            .Select(x => new GroupedTimelines { Events = x.ToList(), Year = x.Key });

        var total = await categoryCollection.CountDocumentsAsync(combinedFilter);

        return new GroupedTimelineResult
        {
            Total = (int)total,
            Size = pageSize,
            Page = page,
            Items = groupedByYear,
        };
    }

    private static FilterDefinition<TimelineEvent> BuildContinuityFilter(Continuity? continuity)
    {
        if (continuity == null || continuity == Continuity.Both)
        {
            // Show both Canon and Legends content
            return Builders<TimelineEvent>.Filter.In(
                x => x.Continuity,
                new[] { Continuity.Canon, Continuity.Legends, Continuity.Both }
            );
        }

        // Filter by specific continuity
        return Builders<TimelineEvent>.Filter.Eq(x => x.Continuity, continuity.Value);
    }

    private static FilterDefinition<TimelineEvent> BuildUniverseFilter(Universe? universe)
    {
        if (universe == null)
        {
            // No filter — return all content
            return Builders<TimelineEvent>.Filter.Empty;
        }

        // Include Unknown documents alongside the requested universe,
        // since most timeline events don't have Universe explicitly set.
        return Builders<TimelineEvent>.Filter.In(
            x => x.Universe,
            new[] { universe.Value, Universe.Unknown }
        );
    }

    public async Task<List<string>> GetTimelineCategories()
    {
        var names = await _timelineEventsDb.ListCollectionNamesAsync();
        List<string> results = await names.ToListAsync();
        return results.OrderBy(x => x).ToList();
    }

    /// <summary>
    /// Converts a year + demarcation into a linear value where BBY is negative and ABY is positive.
    /// e.g. 19 BBY = -19, 4 ABY = 4, 0 BBY = 0
    /// </summary>
    static double ToLinearYear(float year, Demarcation demarcation) =>
        demarcation == Demarcation.Bby ? -year : year;

    /// <summary>
    /// Builds a MongoDB filter that matches timeline events within a year range.
    /// Uses the $expr operator to compute a linear year from Demarcation and Year fields.
    /// </summary>
    static FilterDefinition<TimelineEvent> BuildYearRangeFilter(
        float fromYear, Demarcation fromDemarcation,
        float toYear, Demarcation toDemarcation)
    {
        var linearFrom = ToLinearYear(fromYear, fromDemarcation);
        var linearTo = ToLinearYear(toYear, toDemarcation);

        // Ensure from <= to on the linear scale
        if (linearFrom > linearTo)
            (linearFrom, linearTo) = (linearTo, linearFrom);

        // Build an $expr that computes a linear year:
        // if Demarcation == "Bby" then -Year else Year
        // then checks linearFrom <= linearYear <= linearTo
        var linearYearExpr = new BsonDocument("$cond", new BsonArray
        {
            new BsonDocument("$eq", new BsonArray { "$Demarcation", "Bby" }),
            new BsonDocument("$multiply", new BsonArray { "$Year", -1 }),
            "$Year"
        });

        var expr = new BsonDocument("$expr", new BsonDocument("$and", new BsonArray
        {
            new BsonDocument("$ne", new BsonArray { "$Year", BsonNull.Value }),
            new BsonDocument("$gte", new BsonArray { linearYearExpr, linearFrom }),
            new BsonDocument("$lte", new BsonArray { linearYearExpr, linearTo }),
        }));

        return new BsonDocumentFilterDefinition<TimelineEvent>(expr);
    }
}
