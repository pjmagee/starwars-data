using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Queries;
using StarWarsData.Services.Helpers;
using StarWarsData.Services.Mongo;

namespace StarWarsData.Services.Data;

public class WarService
{
    private readonly ILogger<WarService> _logger;
    private readonly YearHelper _yearHelper;
    private readonly MongoDefinitions _mongoDefinitions;
    private readonly Settings _settings;
    private readonly MongoClient _mongoClient;

    private IMongoDatabase _mongoDb;

    public WarService(ILogger<WarService> logger, YearHelper yearHelper, MongoDefinitions mongoDefinitions, Settings settings)
    {
        _logger = logger;
        _yearHelper = yearHelper;
        _mongoDefinitions = mongoDefinitions;
        _settings = settings;
        _mongoClient = new MongoClient(settings.MongoConnectionString);
        _mongoDb = _mongoClient.GetDatabase(settings.MongoDbName);
    }
    
    public async Task<PagedChartData<double>> GetWarsByDuration(int page = 1, int pageSize = 20)
    {
        var filter = Builders<BsonDocument>.Filter.And(new[] 
        { 
            Builders<BsonDocument>.Filter.AnyEq("Data.Label", "Beginning"),
            Builders<BsonDocument>.Filter.AnyEq("Data.Label", "End"),
            Builders<BsonDocument>.Filter.Regex("Data.Links.Content", new BsonRegularExpression(new Regex("\\d.*\\s+(ABY|BBY)"))),
            Builders<BsonDocument>.Filter.Regex("Data.Links.Href", new BsonRegularExpression(new Regex("_(ABY|BBY)")))
        });
        
        List<BsonDocument> results = await _mongoDb
            .GetCollection<BsonDocument>("War")
            .Find(filter)
            .Project(_mongoDefinitions.ExcludeRelationships)
            .ToListAsync();

        var allWars = results
            .Select(War.Map)
            .Where(x => x is not null)
            .Select(x => x!)
            .Distinct()
            .OrderByDescending(x => x.Years)
            .ToList();

        var wars = allWars.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        var series = new Series<double>()
        {
            Data = wars.Select(x => x.Years).ToList(),
            Name = "Duration"
        };
        
        return new()
        {
            Page = page,
            PageSize = pageSize,
            Total = allWars.Count,
            ChartData = new()
            {
                Labels = wars.Select(war => war.Name).ToArray(),
                Series = new List<Series<double>>(new []{series })
            }
        };
    }
}