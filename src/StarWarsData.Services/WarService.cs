using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Queries;

namespace StarWarsData.Services;

public class WarService
{
    readonly ILogger<WarService> _logger;
    readonly YearHelper _yearHelper;
    readonly MongoDefinitions _mongoDefinitions;
    IMongoDatabase _db;

    public WarService(
        ILogger<WarService> logger,
        YearHelper yearHelper,
        MongoDefinitions mongoDefinitions,
        IOptions<SettingsOptions> settingsOptions,
        IMongoClient mongoClient
    )
    {
        _logger = logger;
        _yearHelper = yearHelper;
        _mongoDefinitions = mongoDefinitions;
        _db = mongoClient.GetDatabase(settingsOptions.Value.InfoboxDb);
    }

    public async Task<PagedChartData<int>> GetWarsByBattles(int page = 1, int pageSize = 10)
    {
        _logger.LogInformation("Getting wars by battles");

        var filter = Builders<BsonDocument>.Filter.And(
            new[]
            {
                Builders<BsonDocument>.Filter.AnyEq("Data.Label", "Beginning"),
                Builders<BsonDocument>.Filter.AnyEq("Data.Label", "End"),
                Builders<BsonDocument>.Filter.AnyEq("Data.Label", "Major battles"),
            }
        );

        List<BsonDocument> results = await _db.GetCollection<BsonDocument>("War")
            .Find(filter)
            .Project(_mongoDefinitions.ExcludeRelationships)
            .ToListAsync();

        var allWars = results
            .Select(War.Map)
            .Where(x => x is not null)
            .Select(x => x!)
            .Distinct()
            .OrderByDescending(x => x.Battles)
            .ToList();

        var wars = allWars.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return new()
        {
            Page = page,
            PageSize = pageSize,
            Total = allWars.Count,
            ChartData = new()
            {
                Labels = wars.Select(war => war.Name).ToArray(),
                Series =
                [
                    new Series<int>()
                    {
                        Data = wars.Select(x => x.Battles).ToList(),
                        Name = "Battles",
                    },
                ],
            },
        };
    }

    public async Task<PagedChartData<double>> GetWarsByDuration(int page = 1, int pageSize = 20)
    {
        var filter = Builders<BsonDocument>.Filter.And(
            new[]
            {
                Builders<BsonDocument>.Filter.AnyEq("Data.Label", "Beginning"),
                Builders<BsonDocument>.Filter.AnyEq("Data.Label", "End"),
                Builders<BsonDocument>.Filter.Regex(
                    "Data.Links.Content",
                    new BsonRegularExpression(new Regex("\\d.*\\s+(ABY|BBY)"))
                ),
                Builders<BsonDocument>.Filter.Regex(
                    "Data.Links.Href",
                    new BsonRegularExpression(new Regex("_(ABY|BBY)"))
                ),
            }
        );

        List<BsonDocument> results = await _db.GetCollection<BsonDocument>("War")
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
            Name = "Duration",
        };

        return new()
        {
            Page = page,
            PageSize = pageSize,
            Total = allWars.Count,
            ChartData = new()
            {
                Labels = wars.Select(war => war.Name).ToArray(),
                Series = [series],
            },
        };
    }
}
