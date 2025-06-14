using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
        int page = 1,
        int pageSize = 20
    )
    {
        var allTimelineEvents = new List<TimelineEvent>();

        // Get all available collections from timeline-events db
        var availableCollections = await GetTimelineCategories();

        // If templates are specified, filter collections; otherwise use all
        var collectionsToQuery = templates.Any()
            ? availableCollections
                .Where(collection =>
                    templates.Contains(_templateHelper.GetTemplateFromUri(collection))
                )
                .ToList()
            : availableCollections;

        // Build continuity filter
        var continuityFilter = BuildContinuityFilter(continuity);

        // Query each relevant collection and combine results
        foreach (var collectionName in collectionsToQuery)
        {
            var collection = _timelineEventsDb.GetCollection<TimelineEvent>(collectionName);
            var events = await collection.Find(continuityFilter).ToListAsync();
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
        int page = 1,
        int pageSize = 20
    )
    {
        var categoryCollection = _timelineEventsDb.GetCollection<TimelineEvent>(category);

        // Build continuity filter
        var continuityFilter = BuildContinuityFilter(continuity);

        var sort = Builders<TimelineEvent>
            .Sort.Ascending(x => x.Demarcation)
            .Ascending(x => x.Year);

        var timelineEventDocuments = await categoryCollection
            .Find(continuityFilter)
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

        var total = await categoryCollection.CountDocumentsAsync(continuityFilter);

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

    public async Task<List<string>> GetTimelineCategories()
    {
        var names = await _timelineEventsDb.ListCollectionNamesAsync();
        List<string> results = await names.ToListAsync();
        return results.OrderBy(x => x).ToList();
    }
}
