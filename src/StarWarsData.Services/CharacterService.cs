using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Queries;

namespace StarWarsData.Services;

public class CharacterService
{
    readonly ILogger<CharacterService> _logger;
    readonly YearHelper _yearHelper;
    readonly MongoDefinitions _mongoDefinitions;
    readonly SettingsOptions _settingsOptions;
    readonly IMongoDatabase _db;

    FilterDefinition<BsonDocument> BornAndDied =>
        Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.AnyEq("Data.Label", "Born"),
            Builders<BsonDocument>.Filter.AnyEq("Data.Label", "Died"),
            _mongoDefinitions.DataLinksContentYear,
            _mongoDefinitions.DataLinksHrefYear
        );

    public CharacterService(
        ILogger<CharacterService> logger,
        YearHelper yearHelper,
        IMongoClient client,
        MongoDefinitions mongoDefinitions,
        IOptions<SettingsOptions> settingsOptions
    )
    {
        _logger = logger;
        _yearHelper = yearHelper;
        _mongoDefinitions = mongoDefinitions;
        _settingsOptions = settingsOptions.Value;
        _db = client.GetDatabase(_settingsOptions.InfoboxDb);
    }

    public async Task<PagedChartData<double>> GetLifeSpans(int page = 1, int pageSize = 20)
    {
        List<BsonDocument> results = await _db.GetCollection<BsonDocument>("Character")
            .Find(BornAndDied)
            .Project(_mongoDefinitions.ExcludeRelationships)
            .ToListAsync();

        var characters = results
            .Select(Character.Map)
            .Where(x => x is not null)
            .Distinct()
            .OrderByDescending(x => x.Years)
            .ToList();

        var items = characters.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return new PagedChartData<double>
        {
            Page = page,
            PageSize = pageSize,
            Total = characters.Count,
            ChartData = new ChartData<double>
            {
                Labels = items.Select(characterAge => characterAge.Name).ToArray(),
                Series = new List<Series<double>>(
                    new[]
                    {
                        new Series<double>()
                        {
                            Data = items.Select(x => (double)x.Years).ToList(),
                            Name = "Ages",
                        },
                    }
                ),
            },
        };
    }

    public async Task<PagedChartData<int>> GetBirthAndDeathsByYear(int page = 1, int pageSize = 20)
    {
        List<BsonDocument> results = await _db.GetCollection<BsonDocument>("Character")
            .Find(BornAndDied)
            .Project(_mongoDefinitions.ExcludeRelationships)
            .ToListAsync();

        var characters = results
            .Select(Character.Map)
            .Where(x => x is not null)
            .Select(x => x!)
            .Distinct()
            .ToList();

        var allYears = characters
            .Select(character => character.Born)
            .Concat(characters.Select(x => x.Died))
            .Where(x => char.IsLetter(x.Last()))
            .Distinct()
            .ToList();

        allYears.Sort(_yearHelper.YearComparer);

        var years = allYears.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        var deaths = years.Select(year => characters.Count(x => x.Died == year)).ToList();
        var births = years.Select(year => characters.Count(x => x.Born == year)).ToList();

        return new PagedChartData<int>
        {
            Page = page,
            PageSize = pageSize,
            Total = allYears.Count,
            ChartData = new()
            {
                Labels = years.ToArray(),
                Series =
                [
                    new() { Data = deaths, Name = "Known deaths" },
                    new() { Data = births, Name = "Known births" },
                ],
            },
        };
    }
}
