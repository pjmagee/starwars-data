using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries; // for CharacterSearchDto
using MongoDB.Bson.Serialization.Attributes; // Required for BsonId if not already present in Record model
using MongoDB.Driver.Linq;
using System.Text.RegularExpressions;

namespace StarWarsData.Services.Data
{
    public class CharacterRelationsService
    {
        private readonly ILogger<CharacterRelationsService> _logger;
        private readonly IMongoDatabase _mongoDb;

        public CharacterRelationsService(ILogger<CharacterRelationsService> logger, IMongoDatabase mongoDb)
        {
            _logger = logger;
            _mongoDb = mongoDb;
        }

        // Removed old name-based search in favor of SearchCharactersAsync

        public async Task<CharacterRelationsDto?> GetRelationsByNameAsync(string name)
        {
            try
            {
                var collection = _mongoDb.GetCollection<Record>("Character"); // Changed BsonDocument to Record
                // Find the record where any InfoboxProperty in Data has Label "Titles" and a Value matching the name.
                var record = await collection.Find(r => r.Data.Any(ip => ip.Label == "Titles" && ip.Values.Contains(name))).FirstOrDefaultAsync();

                if (record == null) return null;

                // Helper to get a single string value from a specific label
                string GetSingleValue(string label, string defaultValue = "") =>
                    record.Data.FirstOrDefault(ip => ip.Label == label)?.Values.FirstOrDefault() ?? defaultValue;

                // Helper to get a list of string values from a specific label
                List<string> GetValueList(string label) =>
                    record.Data.FirstOrDefault(ip => ip.Label == label)?.Values ?? new List<string>();

                return new CharacterRelationsDto
                {
                    Name = name, // Or use record.PageTitle if that's more appropriate
                    Born = GetSingleValue("Born"),
                    Died = GetSingleValue("Died"),
                    Family = GetValueList("Family"),
                    Parents = GetValueList("Parents"),
                    Partners = GetValueList("Partners"),
                    Siblings = GetValueList("Siblings"),
                    Children = GetValueList("Children"),
                };
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Error fetching relations for {Name}", name);
                return null;
            }
        }

        /// <summary>
        /// Fetch detailed relations for a character by its ID.
        /// </summary>
        public async Task<CharacterRelationsDto?> GetRelationsByIdAsync(int id)
        {
            try
            {
                var collection = _mongoDb.GetCollection<Record>("Character");
               
                var record = await collection.Find(r => r.PageId == id).FirstOrDefaultAsync();
                
                if (record == null)
                {
                    _logger.LogWarning("Character with ID {Id} not found.", id);
                    return null;
                }

                var characterName = GetValuesFromData(record, "Titles");

                var dto = new CharacterRelationsDto
                {
                    Name = string.Join("\n ", characterName),
                    Born = GetValuesFromData(record, "Born").FirstOrDefault() ?? string.Empty,
                    Died = GetValuesFromData(record, "Died").FirstOrDefault() ?? string.Empty,
                    Parents = await GetRelatedFromLinksAsync(record, "Parent(s)"),
                    Partners = await GetRelatedFromLinksAsync(record, "Partner(s)"),
                    Siblings = await GetRelatedFromLinksAsync(record, "Sibling(s)"),
                    Children = await GetRelatedFromLinksAsync(record, "Children"),
                };

                return dto;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching relations by ID {Id}", id);
                return null;
            }
        }

        List<string> GetValuesFromData(Record record, string label)
        {
            var property = record.Data.FirstOrDefault(ib => ib.Label != null && ib.Label.Equals(label, StringComparison.OrdinalIgnoreCase));
            return property?.Values ?? new List<string>();
        }
        
        async Task<List<string>> GetRelatedFromLinksAsync(Record record, string label)
        {
            var names = new List<string>();
                    
            var property = record.Data.FirstOrDefault(ib => ib.Label != null && ib.Label.Equals(label, StringComparison.OrdinalIgnoreCase));

            if (property?.Links != null)
            {
                foreach (var link in property.Links)
                {
                    if (!string.IsNullOrWhiteSpace(link.Href))
                    {
                        Record? target = await _mongoDb.GetCollection<Record>("Character").Find(r => r.PageUrl == link.Href).FirstOrDefaultAsync();
                                    
                        if (target != null)
                        {
                            GetValuesFromData(target, "Titles").ForEach(name => names.Add(name));
                        }
                    }
                }
            }
                    
            return names.Distinct().ToList();
        }
        
        /// <summary>
        /// Search characters by title, returning id + name DTOs.
        /// </summary>
        public async Task<List<CharacterSearchDto>> FindCharactersAsync(string search)
        {
            search = search.Trim();
            var collection = _mongoDb.GetCollection<Record>("Character");
            var filterBuilder = Builders<Record>.Filter;
            var filter = filterBuilder.ElemMatch(r => r.Data,
                Builders<InfoboxProperty>.Filter.And(
                    Builders<InfoboxProperty>.Filter.Eq(ip => ip.Label, "Titles"),
                    Builders<InfoboxProperty>.Filter.Regex(ip => ip.Values, new BsonRegularExpression(search, "i"))
                )
            );

            var records = await collection.Find(filter).ToListAsync();

            var results = records.Select(r => new CharacterSearchDto
            {
                Id = r.PageId,
                Name = r.Data.First(ip => ip.Label == "Titles").Values.First()
            })
            .ToList();

            return results;
        }
    }
}