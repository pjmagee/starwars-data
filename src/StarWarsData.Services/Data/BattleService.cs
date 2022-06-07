using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Queries;
using StarWarsData.Services.Helpers;

namespace StarWarsData.Services.Data;

public class BattleService
{
    private readonly ILogger<BattleService> _logger;
    private readonly YearHelper _yearHelper;
    private readonly Settings _settings;
    private readonly MongoClient _mongoClient;

    private IMongoDatabase _mongoDb;

    public BattleService(ILogger<BattleService> logger, YearHelper yearHelper, Settings settings)
    {
        _logger = logger;
        _yearHelper = yearHelper;
        _settings = settings;
        _mongoClient = new MongoClient(settings.MongoConnectionString);
        _mongoDb = _mongoClient.GetDatabase(settings.MongoDbName);
    }

    private string MapToFaction(string outcome)
    {
        if (outcome.Contains("Empire", StringComparison.OrdinalIgnoreCase) ||
            outcome.Contains("Imperial", StringComparison.OrdinalIgnoreCase)) return "Empire";

        if (outcome.Contains("Rebel", StringComparison.OrdinalIgnoreCase) ||
            outcome.Contains("Resistance", StringComparison.OrdinalIgnoreCase) ||
            outcome.Contains("Republic", StringComparison.OrdinalIgnoreCase) ||
            outcome.Contains("Alliance", StringComparison.OrdinalIgnoreCase)) return "Republic";

        if (outcome.Contains("Mandalorian", StringComparison.OrdinalIgnoreCase)) return "Mandalorian";

        if (outcome.Contains("Jedi", StringComparison.OrdinalIgnoreCase)) return "Jedi";
        if (outcome.Contains("Sith", StringComparison.OrdinalIgnoreCase)) return "Sith";

        return "Other factions";
    }

    public async Task<PagedChartData<int>> GetBattlesByYear(int page = 1, int pageSize = 20)
    {
        List<BsonDocument> results = await _mongoDb
            .GetCollection<BsonDocument>("Battle")
            .Find(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.AnyEq("Data.Label", "Date"),
                    Builders<BsonDocument>.Filter.AnyEq("Data.Label", "Outcome"),
                    Builders<BsonDocument>.Filter.Regex("Data.Links.Content",
                        new BsonRegularExpression(new Regex("\\d.*\\s+(ABY|BBY)"))),
                    Builders<BsonDocument>.Filter.Regex("Data.Links.Href",
                        new BsonRegularExpression(new Regex("_(ABY|BBY)")))
                )
            )
            .Project(Builders<BsonDocument>.Projection.Exclude(doc => doc["Relationships"]))
            .ToListAsync();

        var victories = results.SelectMany(ToVictories).Distinct().ToList();
        var factions = victories.GroupBy(x => x.Outcome);
        var years = victories.Select(v => v.Date).Distinct().OrderBy(date => date, _yearHelper.YearComparer).ToList();
        var items = years.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        var seriesList = new List<Series<int>>();

        foreach (var faction in factions)
        {
            var series = new Series<int>()
            {
                Name = faction.Key,
                Data = items.Select(year => faction.Count(f => f.Date == year)).ToList()
            };

            if (series.Data.Any())
            {
                seriesList.Add(series);
            }
        }

        return new()
        {
            Page = page,
            PageSize = pageSize,
            Total = years.Count,
            ChartData = new()
            {
                Labels = items.ToArray(),
                Series = seriesList
            }
        };
    }

    private IEnumerable<Victory> ToVictories(BsonDocument document)
    {
        var name = document["Data"].AsBsonArray.First(i => i["Label"] == "Titles")["Values"][0].AsString;
        var date = document["Data"].AsBsonArray.First(i => i["Label"] == "Date")["Links"][0].AsBsonValue;
        var outcomes = document["Data"].AsBsonArray.First(i => i["Label"] == "Outcome")["Values"];
        var link = document["PageUrl"].AsString;

        if (_yearHelper.IsValidLink(date))
        {
            return outcomes.AsBsonArray.Where(x => x.AsString.Contains("victory", StringComparison.OrdinalIgnoreCase))
                .Select(x => new Victory
                    { Date = date["Content"].AsString, Name = name, Outcome = MapToFaction(x.AsString), Link = link });
        }

        return Enumerable.Empty<Victory>();
    }
}