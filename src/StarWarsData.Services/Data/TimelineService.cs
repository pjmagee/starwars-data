using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Mongo;
using StarWarsData.Models.Queries;
using StarWarsData.Services.Mongo;

namespace StarWarsData.Services.Data;

public class TimelineService
{
    private readonly ILogger<TimelineService> _logger;
    private readonly Settings _settings;
    private readonly MongoClient _mongoClient;
    private readonly RecordToEventsTransformer _transformer;
    private readonly MongoDefinitions _mongoDefinitions;
    private readonly CollectionFilters _collectionFilters;

    private IMongoDatabase _mongoDb;

    public TimelineService(ILogger<TimelineService> logger, Settings settings, RecordToEventsTransformer transformer, MongoDefinitions mongoDefinitions, CollectionFilters collectionFilters)
    {
        _logger = logger;
        _settings = settings;
        _transformer = transformer;
        _mongoDefinitions = mongoDefinitions;
        _collectionFilters = collectionFilters;
        _mongoClient = new MongoClient(settings.MongoConnectionString);
        _mongoDb = _mongoClient.GetDatabase(settings.MongoDbName);
    }
    
    public async Task<GroupedTimelineResult> GetTimelineEvents(IEnumerable<string> collections, int page = 1, int pageSize = 20)
    {
        List<Record> records = new List<Record>();

        foreach (var collection in collections)
        {
            if (_collectionFilters.ContainsKey(collection))
            {
                records.AddRange(await _mongoDb.GetCollection<BsonDocument>(collection)
                    .Find(_collectionFilters[collection])
                    .Project(_mongoDefinitions.ExcludeRelationships)
                    .As<Record>()
                    .ToListAsync());
            }
        }

        var timelineEvents = records
            .AsParallel()
            .SelectMany(r => _transformer.Transform(r))
            .OrderBy(x => x)
            .ToList();

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