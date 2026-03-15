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
            IMongoClient mongoClient
        )
        {
            _logger = logger;
            _settingsOptions = settingsOptions;
            _mongoDb = mongoClient.GetDatabase(settingsOptions.Value.PageInfoboxDb);
        }

        /// <summary>
        /// Fetch detailed relations for a character by its ID.
        /// </summary>
        public async Task<CharacterRelationsDto?> GetRelationsByIdAsync(int id)
        {
            try
            {
                var collection = _mongoDb.GetCollection<Infobox>("Character");
                var record = await collection.Find(r => r.PageId == id).FirstOrDefaultAsync();
                if (record == null)
                {
                    _logger.LogWarning("Character with ID {Id} not found.", id);
                    return null;
                }

                var dto = new CharacterRelationsDto
                {
                    Id = record.PageId,
                    Name = GetValuesFromData(record, "Titles").FirstOrDefault() ?? string.Empty,
                    Born = GetValuesFromData(record, "Born").FirstOrDefault() ?? string.Empty,
                    Died = GetValuesFromData(record, "Died").FirstOrDefault() ?? string.Empty,
                    ImageUrl = string.Empty, // preserve Image loading logic if any
                };

                // resolve all relationships in parallel
                var parentsTask = ResolveRelatedIdsAsync(record, "Parent(s)");
                var partnersTask = ResolveRelatedIdsAsync(record, "Partner(s)");
                var siblingsTask = ResolveRelatedIdsAsync(record, "Sibling(s)");
                var childrenTask = ResolveRelatedIdsAsync(record, "Children");

                await Task.WhenAll(parentsTask, partnersTask, siblingsTask, childrenTask);

                dto.Parents = parentsTask.Result;
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

        List<string> GetValuesFromData(Infobox record, string label)
        {
            var property = record.Data.FirstOrDefault(ib =>
                ib.Label != null && ib.Label.Equals(label, StringComparison.OrdinalIgnoreCase)
            );
            return property?.Values ?? [];
        }

        /// <summary>
        /// For each link in the given label, find the target Character.PageId.
        /// </summary>
        async Task<List<int>> ResolveRelatedIdsAsync(Infobox record, string label)
        {
            var prop = record.Data.FirstOrDefault(ib =>
                ib.Label != null && ib.Label.Equals(label, StringComparison.OrdinalIgnoreCase)
            );
            if (prop?.Links == null)
                return [];

            var lookupTasks = prop
                .Links.Where(l => !string.IsNullOrWhiteSpace(l.Href))
                .Select(async link =>
                    await _mongoDb
                        .GetCollection<Infobox>("Character")
                        .Find(r => r.WikiUrl == link.Href)
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
            var collection = _mongoDb.GetCollection<Infobox>("Character");
            var filterBuilder = Builders<Infobox>.Filter;
            var filter = filterBuilder.ElemMatch(
                r => r.Data,
                Builders<InfoboxProperty>.Filter.And(
                    Builders<InfoboxProperty>.Filter.Eq(ip => ip.Label, "Titles"),
                    Builders<InfoboxProperty>.Filter.Regex(
                        ip => ip.Values,
                        new BsonRegularExpression(search, "i")
                    )
                )
            );

            var records = await collection.Find(filter).ToListAsync();

            var results = records
                .Select(r => new CharacterSearchDto
                {
                    Id = r.PageId,
                    Name = r.Data.First(ip => ip.Label == "Titles").Values.First(),
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
                new BsonDocument(
                    "$graphLookup",
                    new BsonDocument
                    {
                        { "from", "Character" },
                        { "startWith", "$Data.Links.Href" },
                        { "connectFromField", "Data.Links.Href" },
                        { "connectToField", "PageUrl" },
                        { "as", "FamilyMembers" },
                        { "maxDepth", 1 },
                        {
                            "restrictSearchWithMatch",
                            new BsonDocument(
                                "Data.Label",
                                new BsonDocument(
                                    "$in",
                                    new BsonArray
                                    {
                                        "Parent(s)",
                                        "Partner(s)",
                                        "Sibling(s)",
                                        "Children",
                                    }
                                )
                            )
                        },
                    }
                ),
            };

            var agg = await characters.Aggregate<BsonDocument>(pipeline).FirstOrDefaultAsync();
            if (agg == null)
                return new FamilyGraphDto { RootId = rootId };

            // combine root + related docs
            var docs = new List<BsonDocument> { agg }.Concat(
                agg["FamilyMembers"].AsBsonArray.OfType<BsonDocument>()
            );

            // build a map of pageUrl -> pageId
            var urlToId = docs.ToDictionary(d => d["PageUrl"].AsString, d => d["_id"].AsInt32);

            var nodes = new List<FamilyNodeDto>();

            // project each doc into a FamilyNodeDto
            foreach (var doc in docs)
            {
                var arr = doc["Data"].AsBsonArray.OfType<BsonDocument>().ToList();

                // helper to map link Hrefs -> IDs
                List<int> MapLinks(string label)
                {
                    return arr.Where(p => p["Label"].AsString == label)
                        .SelectMany(p => p["Links"].AsBsonArray.OfType<BsonDocument>())
                        .Select(link => link["Href"].AsString)
                        .Where(h => urlToId.ContainsKey(h))
                        .Select(h => urlToId[h])
                        .Distinct()
                        .ToList();
                }

                var node = new FamilyNodeDto
                {
                    Id = doc["_id"].AsInt32,
                    Name =
                        arr.FirstOrDefault(p => p["Label"].AsString == "Titles")
                            ?["Values"].AsBsonArray.FirstOrDefault()
                            ?.AsString ?? string.Empty,
                    Born =
                        arr.FirstOrDefault(p => p["Label"].AsString == "Born")
                            ?["Values"].AsBsonArray.FirstOrDefault()
                            ?.AsString ?? string.Empty,
                    Died =
                        arr.FirstOrDefault(p => p["Label"].AsString == "Died")
                            ?["Values"].AsBsonArray.FirstOrDefault()
                            ?.AsString ?? string.Empty,
                    ImageUrl = string.Empty,
                    Parents = MapLinks("Parent(s)"),
                    Partners = MapLinks("Partner(s)"),
                    Siblings = MapLinks("Sibling(s)"),
                    Children = MapLinks("Children"),
                };

                nodes.Add(node);
            }

            return new FamilyGraphDto { RootId = rootId, Nodes = nodes };
        }

        /// <summary>
        /// Fetch only immediate family (up 1 generation: parents, down 1 generation: children)
        /// using batched WikiUrl $in queries instead of per-link round-trips.
        /// </summary>
        public async Task<ImmediateFamilyDto> GetImmediateFamilyAsync(int rootId)
        {
            var coll = _mongoDb.GetCollection<Infobox>("Character");
            var root = await coll.Find(r => r.PageId == rootId).FirstOrDefaultAsync();
            if (root == null)
            {
                _logger.LogWarning("Character {Id} not found for ImmediateFamily", rootId);
                return new ImmediateFamilyDto();
            }

            // collect all hrefs needed in one pass, URL-decoded for consistent matching
            List<string> HrefsForLabel(string label) =>
                root.Data
                    .Where(p => p.Label?.Equals(label, StringComparison.OrdinalIgnoreCase) == true)
                    .SelectMany(p => p.Links ?? [])
                    .Select(l => Uri.UnescapeDataString(l.Href ?? ""))
                    .Where(h => !string.IsNullOrWhiteSpace(h))
                    .Distinct()
                    .ToList();

            var parentHrefs   = HrefsForLabel("Parent(s)");
            var siblingHrefs  = HrefsForLabel("Sibling(s)");
            var childrenHrefs = HrefsForLabel("Children");
            var partnerHrefs  = HrefsForLabel("Partner(s)");

            // batch-fetch all related Character docs in one query
            // WikiUrl may be stored encoded (%2F) or decoded — query both forms
            var allHrefs = parentHrefs.Concat(siblingHrefs).Concat(childrenHrefs).Concat(partnerHrefs).Distinct().ToList();
            // Build both decoded and %2F-encoded variants to cover all storage forms
            // e.g. ".../Legends" stored as ".../Legends" or "...%2FLegends"
            var allHrefsVariants = allHrefs
                .SelectMany(h => new[] { h, h.Replace("/Legends", "%2FLegends").Replace("/Canon", "%2FCanon") })
                .Distinct()
                .ToList();
            var related = allHrefs.Count == 0
                ? []
                : await coll.Find(r => allHrefsVariants.Contains(r.WikiUrl!)).ToListAsync();

            // build lookup by decoded WikiUrl
            var byUrl = related
                .Where(r => r.WikiUrl != null)
                .GroupBy(r => Uri.UnescapeDataString(r.WikiUrl!))
                .ToDictionary(g => g.Key, g => g.First());

            List<FamilyNodeDto> Resolve(List<string> hrefs) =>
                hrefs
                    .Where(h => byUrl.ContainsKey(h))
                    .Select(h => byUrl[h])
                    .DistinctBy(r => r.PageId)
                    .Select(MapToFamilyNode)
                    .ToList();

            return new ImmediateFamilyDto
            {
                Root     = MapToFamilyNode(root),
                Parents  = Resolve(parentHrefs),
                Siblings = Resolve(siblingHrefs),
                Children = Resolve(childrenHrefs),
                Partners = Resolve(partnerHrefs),
            };
        }

        /// <summary>
        /// Fetch a multi-generation family tree up to <paramref name="maxDepth"/> relationship hops
        /// from the root. Returns all discovered nodes keyed by PageId, plus the root id.
        /// </summary>
        public async Task<FamilyTreeResult> GetFamilyTreeAsync(int rootId, int maxDepth = 3)
        {
            var coll     = _mongoDb.GetCollection<Infobox>("Character");
            var visited     = new Dictionary<int, FamilyNodeDto>();
            var edges       = new List<FamilyEdge>();
            var bfsDepths   = new Dictionary<int, int> { [rootId] = 0 };
            var generations = new Dictionary<int, int> { [rootId] = 0 };
            var queue       = new Queue<int>();
            queue.Enqueue(rootId);

            while (queue.Count > 0)
            {
                var currentId = queue.Dequeue();
                if (visited.ContainsKey(currentId)) continue;

                var doc = await coll.Find(r => r.PageId == currentId).FirstOrDefaultAsync();
                if (doc == null) continue;

                var node = MapToFamilyNode(doc);
                node.Generation = generations.TryGetValue(currentId, out var gen) ? gen : 0;
                visited[currentId] = node;

                int depth = bfsDepths[currentId];
                if (depth >= maxDepth) continue;

                // Resolve children hrefs → edges (parent → child direction only)
                var childHrefs = doc.Data
                    .Where(p => p.Label?.Equals("Children", StringComparison.OrdinalIgnoreCase) == true)
                    .SelectMany(p => p.Links ?? [])
                    .Select(l => Uri.UnescapeDataString(l.Href ?? ""))
                    .Where(h => !string.IsNullOrWhiteSpace(h))
                    .Distinct()
                    .ToList();

                // Collect all relation hrefs for BFS traversal (all directions)
                var allHrefs = new[] { "Parent(s)", "Partner(s)", "Sibling(s)", "Children" }
                    .SelectMany(label => doc.Data
                        .Where(p => p.Label?.Equals(label, StringComparison.OrdinalIgnoreCase) == true)
                        .SelectMany(p => p.Links ?? [])
                        .Select(l => Uri.UnescapeDataString(l.Href ?? "")))
                    .Where(h => !string.IsNullOrWhiteSpace(h))
                    .Distinct()
                    .ToList();

                if (allHrefs.Count == 0) continue;

                var variants = allHrefs
                    .SelectMany(h => new[] { h, h.Replace("/Legends", "%2FLegends").Replace("/Canon", "%2FCanon") })
                    .Distinct()
                    .ToList();

                var neighbours = await coll
                    .Find(r => variants.Contains(r.WikiUrl!))
                    .Project(r => new { r.PageId, r.WikiUrl })
                    .ToListAsync();

                // Build a WikiUrl→PageId lookup (decoded)
                var urlToId = neighbours
                    .Where(n => n.WikiUrl != null)
                    .ToDictionary(n => Uri.UnescapeDataString(n.WikiUrl!), n => n.PageId);

                // Record parent→child edges for rendering
                foreach (var href in childHrefs)
                {
                    if (urlToId.TryGetValue(href, out var childId) && childId != 0)
                        edges.Add(new FamilyEdge { FromId = currentId, ToId = childId });
                }

                // Collect parent hrefs to determine ancestor direction
                var parentHrefs = doc.Data
                    .Where(p => p.Label?.Equals("Parent(s)", StringComparison.OrdinalIgnoreCase) == true)
                    .SelectMany(p => p.Links ?? [])
                    .Select(l => Uri.UnescapeDataString(l.Href ?? ""))
                    .Where(h => !string.IsNullOrWhiteSpace(h))
                    .ToHashSet();

                int currentGen = generations.TryGetValue(currentId, out var cg) ? cg : 0;

                foreach (var n in neighbours.Where(n => n.PageId != 0 && !visited.ContainsKey(n.PageId)))
                {
                    bfsDepths[n.PageId] = depth + 1;
                    // Determine generation direction: is this neighbour a parent of currentId?
                    var decodedUrl = n.WikiUrl != null ? Uri.UnescapeDataString(n.WikiUrl) : "";
                    int neighbourGen = parentHrefs.Contains(decodedUrl) ? currentGen - 1 : currentGen + 1;
                    if (!generations.ContainsKey(n.PageId))
                        generations[n.PageId] = neighbourGen;
                    queue.Enqueue(n.PageId);
                }
            }

            return new FamilyTreeResult { RootId = rootId, Nodes = visited, Edges = edges };
        }

        /// <summary>
        /// Helper to map a Record to FamilyNodeDto without linking relations
        /// </summary>
        FamilyNodeDto MapToFamilyNode(Infobox rec)
        {
            var node = new FamilyNodeDto
            {
                Id = rec.PageId,
                Name = GetValuesFromData(rec, "Titles").FirstOrDefault() ?? string.Empty,
                Born = GetValuesFromData(rec, "Born").FirstOrDefault() ?? string.Empty,
                Died = GetValuesFromData(rec, "Died").FirstOrDefault() ?? string.Empty,
                ImageUrl = rec.ImageUrl ?? string.Empty,
            };
            return node;
        }
    }
}
