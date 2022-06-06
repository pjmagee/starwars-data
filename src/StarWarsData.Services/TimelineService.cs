using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;

namespace StarWarsData.Services;

public class TimelineService
{
    private readonly ILogger<TimelineService> _logger;
    private readonly Settings _settings;
    private readonly MongoClient _mongoClient;
    private readonly EventTransformer _transformer;
    private readonly CollectionFilters _collectionFilters;

    private IMongoDatabase _mongoDb;

    public TimelineService(ILogger<TimelineService> logger, Settings settings, EventTransformer transformer, CollectionFilters collectionFilters)
    {
        _logger = logger;
        _settings = settings;
        _transformer = transformer;
        _collectionFilters = collectionFilters;
        _mongoClient = new MongoClient(settings.MongoConnectionString);
        _mongoDb = _mongoClient.GetDatabase(settings.MongoDbName);
    }
    
    // Relationships are HUGE because so many category records reference the Star Wars Timeline / Events
    // This is a slim cutdown record to load for the Timeline page...
    public async Task<GroupedTimelineResult> GetTimelineEvents(IEnumerable<string> collections, int page = 1,
        int pageSize = 20)
    {
        List<Record> records = new List<Record>();

        foreach (var collection in collections)
        {
            if (_collectionFilters.ContainsKey(collection))
            {
                records.AddRange(await _mongoDb.GetCollection<BsonDocument>(collection)
                    .Find(_collectionFilters[collection])
                    .Project(Builders<BsonDocument>.Projection.Exclude(doc => doc["Relationships"]))
                    .As<Record>()
                    .ToListAsync());
            }
        }

        var timelineEvents = records
            .AsParallel()
            .SelectMany(r => _transformer.Transform(r))
            .ToList();

        timelineEvents.Sort();

        var groupedByYear = timelineEvents
            .GroupBy(x => x.DisplayYear)
            .Select(x => new GroupedTimelines { Events = x.ToList(), Year = x.Key });

        var total = groupedByYear.Count();

        return new GroupedTimelineResult
        {
            Total = total,
            Size = pageSize,
            Page = page,
            Items = groupedByYear.Skip((page - 1) * pageSize).Take(pageSize)
        };
    }
}