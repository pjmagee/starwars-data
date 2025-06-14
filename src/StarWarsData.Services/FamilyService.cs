using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Queries;

namespace StarWarsData.Services
{
    public class FamilyService
    {
        readonly ILogger<FamilyService> _logger;
        readonly IMongoDatabase _mongoDb;

        public FamilyService(
            ILogger<FamilyService> logger,
            IOptions<SettingsOptions> settingsOptions,
            IMongoClient mongoClient
        )
        {
            _logger = logger;
            _mongoDb = mongoClient.GetDatabase(settingsOptions.Value.InfoboxDb);
        }

        public async Task<List<FamilyDto>> GetAllFamiliesAsync()
        {
            try
            {
                var collection = _mongoDb.GetCollection<BsonDocument>("Character");
                // match documents where Data array contains a document with Label "Family"
                var filter = Builders<BsonDocument>.Filter.ElemMatch(
                    "Data",
                    Builders<BsonDocument>.Filter.Eq("Label", "Family")
                );
                var docs = await collection.Find(filter).ToListAsync();

                var families = docs.Select(doc =>
                    {
                        var dataArray = doc["Data"].AsBsonArray;

                        // Character name from Titles field
                        var name =
                            dataArray
                                .FirstOrDefault(d =>
                                    d.AsBsonDocument.GetValue("Label").ToString() == "Titles"
                                )
                                ?.AsBsonDocument.GetValue("Values")
                                .AsBsonArray.FirstOrDefault()
                                ?.ToString() ?? string.Empty;

                        // Family members list
                        var members =
                            dataArray
                                .FirstOrDefault(d =>
                                    d.AsBsonDocument.GetValue("Label").ToString() == "Family"
                                )
                                ?.AsBsonDocument.GetValue("Values")
                                .AsBsonArray.Select(v => v?.ToString() ?? string.Empty)
                                .Where(s => !string.IsNullOrEmpty(s))
                                .ToList() ?? [];

                        return new FamilyDto { Name = name, Members = members };
                    })
                    .Where(f => !string.IsNullOrEmpty(f.Name))
                    .ToList();

                return families;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching families");
                return [];
            }
        }
    }
}
