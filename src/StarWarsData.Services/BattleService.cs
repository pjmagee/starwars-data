using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;

namespace StarWarsData.Services;

public partial class BattleService
{
    private readonly ILogger<BattleService> _logger;
    private readonly Settings _settings;
    private readonly MongoClient _mongoClient;

    private IMongoDatabase _mongoDb;

    public BattleService(ILogger<BattleService> logger, Settings settings)
    {
        _logger = logger;
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

    private bool IsValidDate(BsonValue value)
    {
        if (string.IsNullOrWhiteSpace(value["Content"].AsString))
            return false;

        var content = value["Content"].AsString;
        var link = value["Href"].AsString;

        var containsYear = char.IsDigit(value["Content"].AsString.First());
        var containsDemarcation = content.Contains("BBY") || content.Contains("ABY");
        var linkContainsDemarcation = link.Contains("_BBY") || link.Contains("_ABY");

        return containsYear && containsDemarcation && linkContainsDemarcation;
    }

    public async Task<PagedChartData> GetChartData(int page = 1, int pageSize = 20)
    {
        List<BsonDocument> results = await _mongoDb
            .GetCollection<BsonDocument>("Battle")
            .Find(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.AnyEq("Data.Label", "Date"),
                    Builders<BsonDocument>.Filter.AnyEq("Data.Label", "Outcome"),
                    Builders<BsonDocument>.Filter.Regex("Data.Links.Content", new BsonRegularExpression(new Regex("\\d.*\\s+(ABY|BBY)"))),
                    Builders<BsonDocument>.Filter.Regex("Data.Links.Href", new BsonRegularExpression(new Regex("_(ABY|BBY)")))
                )
            )
            .Project(Builders<BsonDocument>.Projection.Exclude(doc => doc["Relationships"]))
            .ToListAsync();

        var victories = results.SelectMany(x =>
            {
                var name = x["Data"].AsBsonArray.First(i => i["Label"] == "Titles")["Values"][0].AsString;
                var date = x["Data"].AsBsonArray.First(i => i["Label"] == "Date")["Links"][0].AsBsonValue;
                var outcomes = x["Data"].AsBsonArray.First(i => i["Label"] == "Outcome")["Values"];
                var link = x["PageUrl"].AsString;

                if (IsValidDate(date))
                {
                    return outcomes
                        .AsBsonArray
                        .Where(x => x.AsString.Contains("victory", StringComparison.OrdinalIgnoreCase))
                        .Select(x => new Victory
                        {
                            Date = date["Content"].AsString,
                            Name = name,
                            Outcome = MapToFaction(x.AsString),
                            Link = link
                        });
                }

                return Enumerable.Empty<Victory>();
            })
            .Distinct()
            .ToList();

        var factions = victories.GroupBy(x => x.Outcome);
        var years = victories.Select(v => v.Date).Distinct().OrderBy(x => x, new YearComparer()).ToList();

        var items = years.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        var seriesList = new List<Series<int>>();

        foreach (var faction in factions)
        {
            var series = new Series<int>()
            {
                Data = items.Select(year => faction.Count(f => f.Date == year)).ToList(), Name = faction.Key
            };

            if (series.Data.Any())
            {
                seriesList.Add(series);
            }
        }

        return new PagedChartData
        {
            Page = page,
            PageSize = pageSize,
            Total = years.Count,
            ChartData = new ChartData
            {
                Labels = items.ToArray(),
                Series = seriesList
            }
        };
    }
}