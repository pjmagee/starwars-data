using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries;

namespace StarWarsData.Services
{
    public class CharacterRelationsService
    {
        readonly ILogger<CharacterRelationsService> _logger;
        readonly IOptions<SettingsOptions> _settingsOptions;
        readonly IMongoDatabase _mongoDb;

        public CharacterRelationsService(
            ILogger<CharacterRelationsService> logger,
            IOptions<SettingsOptions> settingsOptions,
            IMongoClient mongoClient)
        {
            _logger = logger;
            _settingsOptions = settingsOptions;
            _mongoDb = mongoClient.GetDatabase(settingsOptions.Value.RawDb);
        }

        /// <summary>
        /// Fetch detailed relations for a character by its ID.
        /// </summary>
        public async Task<CharacterRelationsDto?> GetRelationsByIdAsync(int id)
        {
            try
            {
                var collection = _mongoDb.GetCollection<InfoboxRecord>("Character");
                var record     = await collection.Find(r => r.PageId == id).FirstOrDefaultAsync();
                if (record == null)
                {
                    _logger.LogWarning("Character with ID {Id} not found.", id);
                    return null;
                }

                var dto = new CharacterRelationsDto
                {
                    Id        = record.PageId,
                    Name      = GetValuesFromData(record, "Titles").FirstOrDefault() ?? string.Empty,
                    Born      = GetValuesFromData(record, "Born").FirstOrDefault() ?? string.Empty,
                    Died      = GetValuesFromData(record, "Died").FirstOrDefault() ?? string.Empty,
                    ImageUrl  = string.Empty // preserve Image loading logic if any
                };

                // resolve all relationships in parallel
                var parentsTask  = ResolveRelatedIdsAsync(record, "Parent(s)");
                var partnersTask = ResolveRelatedIdsAsync(record, "Partner(s)");
                var siblingsTask = ResolveRelatedIdsAsync(record, "Sibling(s)");
                var childrenTask = ResolveRelatedIdsAsync(record, "Children");

                await Task.WhenAll(parentsTask, partnersTask, siblingsTask, childrenTask);

                dto.Parents  = parentsTask.Result;
                dto.Partners = partnersTask.Result;
                dto.Siblings = siblingsTask.Result;
                dto.Children = childrenTask.Result;

                return dto;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching relations by ID {Id}", id);
                return null;
            }
        }

        List<string> GetValuesFromData(InfoboxRecord record, string label)
        {
            var property = record.Data.FirstOrDefault(ib => ib.Label != null && ib.Label.Equals(label, StringComparison.OrdinalIgnoreCase));
            return property?.Values ?? new List<string>();
        }
        
        /// <summary>
        /// For each link in the given label, find the target Character.PageId.
        /// </summary>
        async Task<List<int>> ResolveRelatedIdsAsync(InfoboxRecord record, string label)
        {
            var prop = record.Data.FirstOrDefault(ib => ib.Label != null && ib.Label.Equals(label, StringComparison.OrdinalIgnoreCase));
            if (prop?.Links == null)
                return new List<int>();

            var lookupTasks = prop.Links
                .Where(l => !string.IsNullOrWhiteSpace(l.Href))
                .Select(async link => await _mongoDb.GetCollection<InfoboxRecord>("Character")
                    .Find(r => r.PageUrl == link.Href)
                    .Project(r => r.PageId)
                    .FirstOrDefaultAsync()
                );

            var ids = await Task.WhenAll(lookupTasks);
            return ids.Distinct().Where(i => i != 0).ToList();
        }
        
        /// <summary>
        /// Search characters by title, returning id + name DTOs.
        /// </summary>
        public async Task<List<CharacterSearchDto>> FindCharactersAsync(string search)
        {
            search = search.Trim();
            var collection = _mongoDb.GetCollection<InfoboxRecord>("Character");
            var filterBuilder = Builders<InfoboxRecord>.Filter;
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

        /// <summary>
        /// Fetch the full family graph (ancestors+descendants) in one aggregation.
        /// </summary>
        public async Task<FamilyGraphDto> GetFamilyGraphAsync(int rootId)
        {
            var characters = _mongoDb.GetCollection<BsonDocument>("Character");
            
            var pipeline = new[]
            {
                new BsonDocument("$match", new BsonDocument("_id", rootId)),
                new BsonDocument("$graphLookup", new BsonDocument
                {
                    { "from", "Character" },
                    { "startWith", "$Data.Links.Href" },
                    { "connectFromField", "Data.Links.Href" },
                    { "connectToField", "PageUrl" },
                    { "as", "FamilyMembers" },
                    { "maxDepth", 1 },
                    { "restrictSearchWithMatch", new BsonDocument(
                        "Data.Label",
                        new BsonDocument("$in", new BsonArray { "Parent(s)", "Partner(s)", "Sibling(s)", "Children" })
                    ) }
                })
            };

            var agg = await characters.Aggregate<BsonDocument>(pipeline).FirstOrDefaultAsync();
            if (agg == null)
                return new FamilyGraphDto { RootId = rootId };

            // combine root + related docs
            var docs = new List<BsonDocument> { agg }
                .Concat(agg["FamilyMembers"].AsBsonArray.OfType<BsonDocument>());

            // build a map of pageUrl -> pageId
            var urlToId = docs.ToDictionary(
                d => d["PageUrl"].AsString,
                d => d["_id"].AsInt32
            );

            var nodes = new List<FamilyNodeDto>();

            // project each doc into a FamilyNodeDto
            foreach (var doc in docs)
            {
                var arr = doc["Data"].AsBsonArray.OfType<BsonDocument>().ToList();

                // helper to map link Hrefs -> IDs
                List<int> MapLinks(string label)
                {
                    return arr
                        .Where(p => p["Label"].AsString == label)
                        .SelectMany(p => p["Links"].AsBsonArray.OfType<BsonDocument>())
                        .Select(link => link["Href"].AsString)
                        .Where(h => urlToId.ContainsKey(h))
                        .Select(h => urlToId[h])
                        .Distinct()
                        .ToList();
                }

                var node = new FamilyNodeDto
                {
                    Id       = doc["_id"].AsInt32,
                    Name     = arr.FirstOrDefault(p => p["Label"].AsString == "Titles")?["Values"].AsBsonArray.FirstOrDefault()?.AsString ?? string.Empty,
                    Born     = arr.FirstOrDefault(p => p["Label"].AsString == "Born")?["Values"].AsBsonArray.FirstOrDefault()?.AsString ?? string.Empty,
                    Died     = arr.FirstOrDefault(p => p["Label"].AsString == "Died")?["Values"].AsBsonArray.FirstOrDefault()?.AsString ?? string.Empty,
                    ImageUrl = string.Empty,
                    Parents  = MapLinks("Parent(s)"),
                    Partners = MapLinks("Partner(s)"),
                    Siblings = MapLinks("Sibling(s)"),
                    Children = MapLinks("Children")
                };

                nodes.Add(node);
            }

            return new FamilyGraphDto
            {
                RootId = rootId,
                Nodes  = nodes
            };
        }

        /// <summary>
        /// Fetch only the immediate family members for a character (parents, partners, siblings, children)
        /// </summary>
        public async Task<ImmediateFamilyDto> GetImmediateFamilyAsync(int rootId)
        {
            var coll = _mongoDb.GetCollection<InfoboxRecord>("Character");
            var root = await coll.Find(r => r.PageId == rootId).FirstOrDefaultAsync();
            if (root == null)
            {
                _logger.LogWarning("Character {Id} not found for ImmediateFamily", rootId);
                return new ImmediateFamilyDto();
            }

            // map root data
            var dto = new ImmediateFamilyDto
            {
                Root = MapToFamilyNode(root)
            };

            // helper to resolve links by label to full FamilyNodeDto
            async Task<List<FamilyNodeDto>> resolve(string label)
            {
                var prop = root.Data.FirstOrDefault(p => p.Label?.Equals(label, StringComparison.OrdinalIgnoreCase) == true);
                if (prop?.Links == null) return new List<FamilyNodeDto>();

                var seenIds = new HashSet<int>();
                var list = new List<FamilyNodeDto>();

                foreach (var lnk in prop.Links.Where(l => !string.IsNullOrWhiteSpace(l.Href)))
                {
                    if (!lnk.Href.Contains("/Legends"))
                        continue;
                    var rec = await coll.Find(r => r.PageUrl == lnk.Href).FirstOrDefaultAsync();
                    if (rec != null && seenIds.Add(rec.PageId))
                    {
                        list.Add(MapToFamilyNode(rec));
                    }
                }

                return list;
            }

            // populate each relationship list
            dto.Parents  = await resolve("Parent(s)");
            dto.Partners = await resolve("Partner(s)");
            dto.Siblings = await resolve("Sibling(s)");
            dto.Children = await resolve("Children");

            return dto;
        }

        /// <summary>
        /// Helper to map a Record to FamilyNodeDto without linking relations
        /// </summary>
        FamilyNodeDto MapToFamilyNode(InfoboxRecord rec)
        {
            var node = new FamilyNodeDto
            {
                Id       = rec.PageId,
                Name     = GetValuesFromData(rec, "Titles").FirstOrDefault() ?? string.Empty,
                Born     = GetValuesFromData(rec, "Born").FirstOrDefault() ?? string.Empty,
                Died     = GetValuesFromData(rec, "Died").FirstOrDefault() ?? string.Empty,
                ImageUrl = rec.ImageUrl ?? string.Empty
            };
            return node;
        }
    }
}