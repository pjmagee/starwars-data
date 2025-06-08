using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Queries;
using TimelineEvent = StarWarsData.Models.Entities.TimelineEvent; // Add using for TemplateHelper

namespace StarWarsData.Services;

public class TimelineService
{
    readonly ILogger<TimelineService> _logger;
    readonly IMongoCollection<TimelineEvent> _timelineEventsCollection;
    readonly TemplateHelper _templateHelper;
    readonly IMongoDatabase _structuredDb;

    public TimelineService(
        ILogger<TimelineService> logger, 
        IOptions<SettingsOptions> settingsOptions,
        IMongoClient mongoClient,
        TemplateHelper templateHelper)
    {
        _logger = logger;
        _templateHelper = templateHelper;
        _structuredDb = mongoClient.GetDatabase(settingsOptions.Value.StructuredDb);
        _timelineEventsCollection = _structuredDb.GetCollection<TimelineEvent>("Timeline_events");
    }
    
    public async Task<GroupedTimelineResult> GetTimelineEvents(IList<string> templates, int page = 1, int pageSize = 20)
    {
        var filter = Builders<TimelineEvent>.Filter.Empty;
        
        if (templates.Any())
        {
            // Filter using the CleanedTemplate field
            filter = Builders<TimelineEvent>.Filter.In(x => x.CleanedTemplate, templates);
        }

        var sort = Builders<TimelineEvent>.Sort.Ascending(x => x.Demarcation).Ascending(x => x.Year);
        var timelineEventDocuments = await _timelineEventsCollection
            .Find(filter)
            .Sort(sort)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync();

        var timelineEvents = timelineEventDocuments.Select(doc => new TimelineEvent
        {
            Title = doc.Title,
            Template = doc.CleanedTemplate,
            ImageUrl = doc.ImageUrl,
            Demarcation = doc.Demarcation,
            Year = doc.Year,
            Properties = doc.Properties,
            DateEvent = doc.DateEvent
        }).ToList();

        timelineEvents.Sort();

        var groupedByYear = timelineEvents
            .GroupBy(x => x.DisplayYear)
            .Select(x => new GroupedTimelines { Events = x.ToList(), Year = x.Key });

        var total = await _timelineEventsCollection.CountDocumentsAsync(filter);

        return new GroupedTimelineResult
        {
            Total = (int)total,
            Size = pageSize,
            Page = page,
            Items = groupedByYear
        };
    }

    public async Task<List<string>> GetDistinctTemplatesAsync()
    {
        var templates = await _timelineEventsCollection.Distinct<string>("Template", FilterDefinition<TimelineEvent>.Empty).ToListAsync();
        return templates.Select(_templateHelper.CleanTemplate).Distinct().OrderBy(x => x).ToList(); 
    }
}
