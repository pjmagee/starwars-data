using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Queries;
using StarWarsData.Services.Helpers;

namespace StarWarsData.Services.Data;

public class PowerService
{
    private readonly ILogger<PowerService> _logger;
    private readonly Settings _settings;
    private readonly MongoClient _mongoClient;

    private IMongoDatabase _mongoDb;

    public PowerService(ILogger<PowerService> logger, Settings settings)
    {
        _logger = logger;
        _settings = settings;
        _mongoClient = new MongoClient(settings.MongoConnectionString);
        _mongoDb = _mongoClient.GetDatabase(settings.MongoDbName);
    }

    public async Task<ChartData<int>> GetPowersChart()
    {
        List<BsonDocument> results = await _mongoDb
            .GetCollection<BsonDocument>("Force_power")
            .Find(Builders<BsonDocument>.Filter.AnyEq("Data.Label", "Titles"))
            .Project(Builders<BsonDocument>.Projection.Exclude(doc => doc["Relationships"]))
            .ToListAsync();

        var powers = results.Select(x =>
            {
                try
                {
                    var name = x["Data"].AsBsonArray.First(i => i["Label"] == "Titles")["Values"][0].ToString();

                    var alignmentData = x["Data"].AsBsonArray.FirstOrDefault(i => i["Label"] == "Alignment");
                    var affiliationData = x["Data"].AsBsonArray
                        .FirstOrDefault(i => i["Label"].AsString.Contains("Affilation"));

                    var alignments = new HashSet<string>();
                    var areaValues = new HashSet<string>();

                    if (alignmentData is not null)
                    {
                        foreach (var item in alignmentData["Values"].AsBsonArray.Select(x => x.AsString))
                        {
                            if (item.Contains("Light", StringComparison.OrdinalIgnoreCase))
                            {
                                alignments.Add("Light");
                            }

                            if (item.Contains("Neutral", StringComparison.OrdinalIgnoreCase) ||
                                item.Contains("Both", StringComparison.OrdinalIgnoreCase))
                            {
                                alignments.Add("Neutral");
                            }

                            if (item.Contains("Dark", StringComparison.OrdinalIgnoreCase))
                            {
                                alignments.Add("Dark");
                            }
                        }
                    }
                    else
                    {
                        alignments.Add("Neutral");
                    }

                    if (affiliationData is not null)
                    {
                        foreach (var item in affiliationData["Values"].AsBsonArray.Select(x => x.AsString))
                        {
                            if (item.Contains("Sith", StringComparison.OrdinalIgnoreCase))
                            {
                                alignments.Add("Dark");
                            }

                            if (item.Contains("Jedi", StringComparison.OrdinalIgnoreCase))
                            {
                                alignments.Add("Light");
                            }
                        }
                    }


                    var areaItem = x["Data"].AsBsonArray.FirstOrDefault(i => i["Label"] == "Area");
                    var purposeItem = x["Data"].AsBsonArray.FirstOrDefault(i => i["Label"] == "Purpose");

                    areaItem?.AsBsonValue["Values"].AsBsonArray.Select(x => x.AsString).Do(area => areaValues.Add(area));
                    purposeItem?.AsBsonValue["Values"].AsBsonArray.Select(x => x.AsString)                        .Do(purpose => areaValues.Add(purpose));


                    var areas = new List<string>();

                    foreach (var item in areaValues)
                    {
                        if (item.Contains("Alter", StringComparison.OrdinalIgnoreCase))
                        {
                            areas.Add("Alter");
                        }

                        if (item.Contains("Sense", StringComparison.OrdinalIgnoreCase))
                        {
                            areas.Add("Sense");
                        }

                        if (item.Contains("Control", StringComparison.OrdinalIgnoreCase))
                        {
                            areas.Add("Control");
                        }
                    }

                    var power = new Power
                    {
                        Name = name,
                        Alignments = alignments.ToList(),
                        Areas = areas
                    };

                    return power;
                }
                catch
                {
                    return null;
                }
            })
            .Where(x => x is not null)
            .Distinct()
            .ToList();
        
        var darkCount = powers.Count(p => p.Alignments.Contains("Dark"));
        var lightCount = powers.Count(p => p.Alignments.Contains("Light"));
        var neutralCount = powers.Count(p => p.Alignments.Contains("Neutral") && !p.Alignments.Contains("Dark") && !p.Alignments.Contains("Light"));

        
        return new ChartData<int>()
        {
            Labels = new[] { $"Dark ({darkCount})", $"Light ({lightCount})", $"Neutral ({neutralCount})" },
            Series = new List<Series<int>>()
            {
                new Series<int>()
                {
                    Data = new List<int>()
                    {
                        powers.Count(p => p.Alignments.Contains("Dark") && p.Areas.Contains("Alter")),
                        powers.Count(p => p.Alignments.Contains("Light") && p.Areas.Contains("Alter")),
                        powers.Count(p => p.Alignments.Contains("Neutral") && p.Areas.Contains("Alter")),
                    } ,
                    Name = "Alter"
                },
                new Series<int>()
                {
                    Data = new List<int>()
                    {
                        powers.Count(p => p.Alignments.Contains("Dark") && p.Areas.Contains("Sense")),
                        powers.Count(p => p.Alignments.Contains("Light") && p.Areas.Contains("Sense")),
                        powers.Count(p => p.Alignments.Contains("Neutral") && p.Areas.Contains("Sense")),
                    } ,
                    Name = "Sense"
                },
                new Series<int>()
                {
                    Data = new List<int>()
                    {
                        powers.Count(p => p.Alignments.Contains("Dark") && p.Areas.Contains("Control")),
                        powers.Count(p => p.Alignments.Contains("Light") && p.Areas.Contains("Control")),
                        powers.Count(p => p.Alignments.Contains("Neutral") && p.Areas.Contains("Control")),
                    } ,
                    Name = "Control"
                }
            }
        };
    }
}