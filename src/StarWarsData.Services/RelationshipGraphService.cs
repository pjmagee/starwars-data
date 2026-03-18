using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries;

namespace StarWarsData.Services
{
    public class RelationshipGraphService
    {
        readonly ILogger<RelationshipGraphService> _logger;
        readonly IMongoCollection<Page> _pages;

        public RelationshipGraphService(
            ILogger<RelationshipGraphService> logger,
            IOptions<SettingsOptions> settingsOptions,
            IMongoClient mongoClient
        )
        {
            _logger = logger;
            _pages = mongoClient.GetDatabase(settingsOptions.Value.PagesDb).GetCollection<Page>("Pages");
        }

        static FilterDefinition<Page> TemplateFilter(string collection) =>
            Builders<Page>.Filter.Regex("infobox.Template", new BsonRegularExpression($":{collection}$", "i"));

        static FilterDefinition<Page> WithTemplate(string collection, FilterDefinition<Page> extra) =>
            Builders<Page>.Filter.And(TemplateFilter(collection), extra);

        /// <summary>
        /// Fetch detailed relations for an entity by its ID within a given type.
        /// </summary>
        public async Task<EntityRelationsDto?> GetRelationsByIdAsync(int id, string collection = "Character")
        {
            try
            {
                var filter = WithTemplate(collection, Builders<Page>.Filter.Eq(p => p.PageId, id));
                var page = await _pages.Find(filter).FirstOrDefaultAsync();
                if (page == null)
                {
                    _logger.LogWarning("Entity with ID {Id} not found in {Collection}.", id, collection);
                    return null;
                }

                var dto = new EntityRelationsDto
                {
                    Id = page.PageId,
                    Name = GetValuesFromPage(page, "Titles").FirstOrDefault() ?? string.Empty,
                    Born = GetValuesFromPage(page, "Born").FirstOrDefault() ?? string.Empty,
                    Died = GetValuesFromPage(page, "Died").FirstOrDefault() ?? string.Empty,
                    ImageUrl = page.Infobox?.ImageUrl ?? string.Empty,
                };

                var parentsTask = ResolveRelatedIdsAsync(page, "Parent(s)", collection);
                var partnersTask = ResolveRelatedIdsAsync(page, "Partner(s)", collection);
                var siblingsTask = ResolveRelatedIdsAsync(page, "Sibling(s)", collection);
                var childrenTask = ResolveRelatedIdsAsync(page, "Children", collection);

                await Task.WhenAll(parentsTask, partnersTask, siblingsTask, childrenTask);

                dto.Parents = parentsTask.Result;
                dto.Partners = partnersTask.Result;
                dto.Siblings = siblingsTask.Result;
                dto.Children = childrenTask.Result;

                return dto;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching relations by ID {Id} in {Collection}", id, collection);
                return null;
            }
        }

        static List<string> GetValuesFromPage(Page page, string label)
        {
            var property = page.Infobox?.Data.FirstOrDefault(ib =>
                ib.Label != null && ib.Label.Equals(label, StringComparison.OrdinalIgnoreCase)
            );
            return property?.Values ?? [];
        }

        async Task<List<int>> ResolveRelatedIdsAsync(Page page, string label, string collection = "Character")
        {
            var prop = page.Infobox?.Data.FirstOrDefault(ib =>
                ib.Label != null && ib.Label.Equals(label, StringComparison.OrdinalIgnoreCase)
            );
            if (prop?.Links == null)
                return [];

            var hrefs = prop.Links
                .Where(l => !string.IsNullOrWhiteSpace(l.Href))
                .Select(l => l.Href)
                .ToList();

            if (hrefs.Count == 0) return [];

            var templateFilter = TemplateFilter(collection);
            var urlFilter = Builders<Page>.Filter.In(p => p.WikiUrl, hrefs);
            var combined = Builders<Page>.Filter.And(templateFilter, urlFilter);

            var matches = await _pages.Find(combined)
                .Project(p => p.PageId)
                .ToListAsync();

            return matches.Distinct().Where(i => i != 0).ToList();
        }

        /// <summary>
        /// Search entities by title within a type, returning id + name DTOs.
        /// </summary>
        public async Task<List<EntitySearchDto>> FindEntitiesAsync(string search, string collection = "Character")
        {
            search = search.Trim();
            var templateFilter = TemplateFilter(collection);
            var titleFilter = Builders<Page>.Filter.ElemMatch<BsonDocument>(
                "infobox.Data",
                new BsonDocument
                {
                    { "Label", "Titles" },
                    { "Values", new BsonDocument("$regex", new BsonRegularExpression(search, "i")) },
                }
            );
            var filter = Builders<Page>.Filter.And(templateFilter, titleFilter);

            var pages = await _pages.Find(filter).ToListAsync();

            return pages
                .Where(p => p.Infobox != null)
                .Select(p => new EntitySearchDto
                {
                    Id = p.PageId,
                    Name = p.Infobox!.Data.FirstOrDefault(ip => ip.Label == "Titles")?.Values.FirstOrDefault() ?? p.Title,
                })
                .ToList();
        }

        /// <summary>
        /// Fetch the full relationship graph (ancestors+descendants) in one aggregation.
        /// </summary>
        public async Task<GraphDto> GetGraphAsync(int rootId, string collection = "Character")
        {
            // For $graphLookup we need to use BsonDocument collection on Pages
            var documents = _pages.Database.GetCollection<BsonDocument>("Pages");
            var templateRegex = new BsonRegularExpression($":{collection}$", "i");

            var pipeline = new[]
            {
                new BsonDocument("$match", new BsonDocument
                {
                    { "_id", rootId },
                    { "infobox.Template", new BsonDocument("$regex", templateRegex) },
                }),
                new BsonDocument(
                    "$graphLookup",
                    new BsonDocument
                    {
                        { "from", "Pages" },
                        { "startWith", "$infobox.Data.Links.Href" },
                        { "connectFromField", "infobox.Data.Links.Href" },
                        { "connectToField", "wikiUrl" },
                        { "as", "FamilyMembers" },
                        { "maxDepth", 1 },
                        {
                            "restrictSearchWithMatch",
                            new BsonDocument
                            {
                                { "infobox.Template", new BsonDocument("$regex", templateRegex) },
                                {
                                    "infobox.Data.Label",
                                    new BsonDocument(
                                        "$in",
                                        new BsonArray { "Parent(s)", "Partner(s)", "Sibling(s)", "Children" }
                                    )
                                },
                            }
                        },
                    }
                ),
            };

            var agg = await documents.Aggregate<BsonDocument>(pipeline).FirstOrDefaultAsync();
            if (agg == null)
                return new GraphDto { RootId = rootId };

            var docs = new List<BsonDocument> { agg }.Concat(
                agg["FamilyMembers"].AsBsonArray.OfType<BsonDocument>()
            );

            var urlToId = docs
                .Where(d => d.Contains("wikiUrl"))
                .ToDictionary(d => d["wikiUrl"].AsString, d => d["_id"].AsInt32);

            var nodes = new List<GraphNodeDto>();

            foreach (var doc in docs)
            {
                var infobox = doc.Contains("infobox") ? doc["infobox"].AsBsonDocument : null;
                var arr = infobox?["Data"].AsBsonArray.OfType<BsonDocument>().ToList() ?? [];

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

                var node = new GraphNodeDto
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
                    ImageUrl = infobox != null && infobox.Contains("ImageUrl") && !infobox["ImageUrl"].IsBsonNull
                        ? infobox["ImageUrl"].AsString : string.Empty,
                    Parents = MapLinks("Parent(s)"),
                    Partners = MapLinks("Partner(s)"),
                    Siblings = MapLinks("Sibling(s)"),
                    Children = MapLinks("Children"),
                };

                nodes.Add(node);
            }

            return new GraphDto { RootId = rootId, Nodes = nodes };
        }

        /// <summary>
        /// Fetch only immediate relations using batched WikiUrl $in queries.
        /// </summary>
        public async Task<ImmediateRelationsDto> GetImmediateRelationsAsync(int rootId, string collection = "Character")
        {
            var filter = WithTemplate(collection, Builders<Page>.Filter.Eq(p => p.PageId, rootId));
            var root = await _pages.Find(filter).FirstOrDefaultAsync();
            if (root == null)
            {
                _logger.LogWarning("Entity {Id} not found in {Collection} for ImmediateRelations", rootId, collection);
                return new ImmediateRelationsDto();
            }

            List<string> HrefsForLabel(string label) =>
                (root.Infobox?.Data ?? [])
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

            var allHrefs = parentHrefs.Concat(siblingHrefs).Concat(childrenHrefs).Concat(partnerHrefs).Distinct().ToList();
            var allHrefsVariants = allHrefs
                .SelectMany(h => new[] { h, h.Replace("/Legends", "%2FLegends").Replace("/Canon", "%2FCanon") })
                .Distinct()
                .ToList();

            var templateFilter = TemplateFilter(collection);
            var related = allHrefs.Count == 0
                ? []
                : await _pages.Find(
                    Builders<Page>.Filter.And(
                        templateFilter,
                        Builders<Page>.Filter.In(p => p.WikiUrl, allHrefsVariants)
                    )
                ).ToListAsync();

            var byUrl = related
                .Where(r => r.WikiUrl != null)
                .GroupBy(r => Uri.UnescapeDataString(r.WikiUrl!))
                .ToDictionary(g => g.Key, g => g.First());

            List<GraphNodeDto> Resolve(List<string> hrefs) =>
                hrefs
                    .Where(h => byUrl.ContainsKey(h))
                    .Select(h => byUrl[h])
                    .DistinctBy(r => r.PageId)
                    .Select(MapToGraphNode)
                    .ToList();

            return new ImmediateRelationsDto
            {
                Root     = MapToGraphNode(root),
                Parents  = Resolve(parentHrefs),
                Siblings = Resolve(siblingHrefs),
                Children = Resolve(childrenHrefs),
                Partners = Resolve(partnerHrefs),
            };
        }

        /// <summary>
        /// Fetch a multi-generation relationship graph up to maxDepth hops from the root.
        /// </summary>
        public async Task<GraphResult> GetRelationshipGraphAsync(int rootId, string collection = "Character", int maxDepth = 3)
        {
            var templateFilter = TemplateFilter(collection);
            var visited     = new Dictionary<int, GraphNodeDto>();
            var edges       = new List<GraphEdge>();
            var bfsDepths   = new Dictionary<int, int> { [rootId] = 0 };
            var generations = new Dictionary<int, int> { [rootId] = 0 };
            var queue       = new Queue<int>();
            queue.Enqueue(rootId);

            while (queue.Count > 0)
            {
                var currentId = queue.Dequeue();
                if (visited.ContainsKey(currentId)) continue;

                var doc = await _pages
                    .Find(Builders<Page>.Filter.And(templateFilter, Builders<Page>.Filter.Eq(p => p.PageId, currentId)))
                    .FirstOrDefaultAsync();
                if (doc == null) continue;

                var node = MapToGraphNode(doc);
                node.Generation = generations.TryGetValue(currentId, out var gen) ? gen : 0;
                visited[currentId] = node;

                int depth = bfsDepths[currentId];
                if (depth >= maxDepth) continue;

                var infoboxData = doc.Infobox?.Data ?? [];

                var childHrefs = infoboxData
                    .Where(p => p.Label?.Equals("Children", StringComparison.OrdinalIgnoreCase) == true)
                    .SelectMany(p => p.Links ?? [])
                    .Select(l => Uri.UnescapeDataString(l.Href ?? ""))
                    .Where(h => !string.IsNullOrWhiteSpace(h))
                    .Distinct()
                    .ToList();

                var allHrefs = new[] { "Parent(s)", "Partner(s)", "Sibling(s)", "Children" }
                    .SelectMany(label => infoboxData
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

                var neighbours = await _pages
                    .Find(Builders<Page>.Filter.And(
                        templateFilter,
                        Builders<Page>.Filter.In(p => p.WikiUrl, variants)
                    ))
                    .Project(p => new { p.PageId, p.WikiUrl })
                    .ToListAsync();

                var urlToId = neighbours
                    .Where(n => n.WikiUrl != null)
                    .ToDictionary(n => Uri.UnescapeDataString(n.WikiUrl!), n => n.PageId);

                foreach (var href in childHrefs)
                {
                    if (urlToId.TryGetValue(href, out var childId) && childId != 0)
                        edges.Add(new GraphEdge { FromId = currentId, ToId = childId });
                }

                var parentHrefs = infoboxData
                    .Where(p => p.Label?.Equals("Parent(s)", StringComparison.OrdinalIgnoreCase) == true)
                    .SelectMany(p => p.Links ?? [])
                    .Select(l => Uri.UnescapeDataString(l.Href ?? ""))
                    .Where(h => !string.IsNullOrWhiteSpace(h))
                    .ToHashSet();

                int currentGen = generations.TryGetValue(currentId, out var cg) ? cg : 0;

                foreach (var n in neighbours.Where(n => n.PageId != 0 && !visited.ContainsKey(n.PageId)))
                {
                    bfsDepths[n.PageId] = depth + 1;
                    var decodedUrl = n.WikiUrl != null ? Uri.UnescapeDataString(n.WikiUrl) : "";
                    int neighbourGen = parentHrefs.Contains(decodedUrl) ? currentGen - 1 : currentGen + 1;
                    if (!generations.ContainsKey(n.PageId))
                        generations[n.PageId] = neighbourGen;
                    queue.Enqueue(n.PageId);
                }
            }

            return new GraphResult { RootId = rootId, Nodes = visited, Edges = edges };
        }

        static GraphNodeDto MapToGraphNode(Page page)
        {
            return new GraphNodeDto
            {
                Id = page.PageId,
                Name = GetValuesFromPage(page, "Titles").FirstOrDefault() ?? string.Empty,
                Born = GetValuesFromPage(page, "Born").FirstOrDefault() ?? string.Empty,
                Died = GetValuesFromPage(page, "Died").FirstOrDefault() ?? string.Empty,
                ImageUrl = page.Infobox?.ImageUrl ?? string.Empty,
            };
        }
    }
}
