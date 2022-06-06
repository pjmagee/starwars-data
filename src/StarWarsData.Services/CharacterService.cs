using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;

namespace StarWarsData.Services;

public class CharacterService
    {
        private readonly ILogger<CharacterService> _logger;
        private readonly Settings _settings;
        private readonly MongoClient _mongoClient;

        private IMongoDatabase _mongoDb;

        public CharacterService(ILogger<CharacterService> logger, Settings settings)
        {
            _logger = logger;
            _settings = settings;
            _mongoClient = new MongoClient(settings.MongoConnectionString);
            _mongoDb = _mongoClient.GetDatabase(settings.MongoDbName);
        }
    
        public async Task<PagedChartData> GetChartData(int page = 1, int pageSize = 20)
        {
            List<BsonDocument> results = await _mongoDb
                .GetCollection<BsonDocument>("Character")
                .Find(
                    Builders<BsonDocument>.Filter.And
                    (
                        Builders<BsonDocument>.Filter.AnyEq("Data.Label", "Born"),
                        Builders<BsonDocument>.Filter.AnyEq("Data.Label", "Died"),
                        Builders<BsonDocument>.Filter.SizeGt("Data.Links", 0),
                        Builders<BsonDocument>.Filter.Exists("Data.Links.Content"),
                        Builders<BsonDocument>.Filter.Regex("Data.Links.Content", new BsonRegularExpression(new Regex("\\d.*\\s+(ABY|BBY)"))),
                        Builders<BsonDocument>.Filter.Regex("Data.Links.Href", new BsonRegularExpression(new Regex("_(ABY|BBY)")))
                    )
                )
                .Project(Builders<BsonDocument>.Projection.Exclude(doc => doc["Relationships"]))
                .ToListAsync();

            var characters = results
                .Select(CharacterAge.From)
                .Where(x => x is not null)
                .Distinct()
                .ToList();

            var years = characters
                .Select(x => x.Born)
                .Concat(characters.Select(x => x.Died))
                .Where(x => char.IsLetter(x.Last()))
                .Distinct()
                .ToList();

            years.Sort((x, y) =>
            {
                if (x.Contains("BBY") && y.Contains("ABY")) return -1;
                if (x.Contains("ABY") && y.Contains("BBY")) return 1;
                return (float.Parse(x.Split(' ')[0]).CompareTo(float.Parse(y.Split(' ')[0])));
            });

            var items = years.Skip((page - 1) * pageSize).Take(pageSize);
            var deaths = items.Select(year => characters.Where(x => x.Died == year).Count()).ToList();
            var births = items.Select(year => characters.Where(x => x.Born == year).Count()).ToList();

            return new PagedChartData
            {
                Page = page,
                PageSize = pageSize,
                Total = years.Count,
                ChartData = new ChartData
                {
                    Labels = items.ToArray(),
                    Series = new List<Series<int>>
                    {
                        new() { Data = deaths, Name = "Deaths" },
                        new() { Data = births, Name = "Births" }
                    }
                }
            };
        }
    }