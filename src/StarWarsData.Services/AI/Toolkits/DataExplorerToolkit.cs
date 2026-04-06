using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services;

/// <summary>
/// AI tools for querying the raw <c>Pages</c> collection.
///
/// All data lives in a single "Pages" collection. Each page may carry an embedded
/// <c>infobox</c> object whose <c>Template</c> field identifies the type
/// (Character, Battle, Species, etc.).
///
/// Every tool takes an <c>infoboxType</c> argument which filters Pages by
/// <c>infobox.Template</c> — it is NOT a MongoDB collection name. Use
/// <c>list_infobox_types</c> to discover valid values.
///
/// Prefer the knowledge-graph toolkits (GraphRAG, KGAnalytics) for entity lookup
/// and analytics. Use this toolkit when you need raw infobox field values that
/// aren't projected into the KG.
/// </summary>
public class DataExplorerToolkit(IMongoClient mongoClient, IOptions<SettingsOptions> settings)
{
    IMongoCollection<BsonDocument> Pages => mongoClient.GetDatabase(settings.Value.DatabaseName).GetCollection<BsonDocument>(Collections.Pages);

    const string InfoboxTypeParamDescription = "Infobox type name — filters Pages by infobox.Template. Use list_infobox_types for valid values.";
    const string ContinuityParamDescription = "Optional continuity filter: Canon, Legends, or omit for all";

    static string EscapeRegex(string input) => System.Text.RegularExpressions.Regex.Escape(input);

    /// <summary>
    /// Builds an equality filter on infobox.Template matching the full template URL.
    /// e.g. "Character" matches "https://starwars.fandom.com/wiki/Template:Character"
    /// </summary>
    static BsonDocument TemplateFilter(string infoboxType) => new(PageBsonFields.InfoboxTemplate, $"{Collections.TemplateUrlPrefix}{infoboxType}");

    static BsonDocument WithTemplate(string infoboxType, BsonDocument extra)
    {
        var filter = TemplateFilter(infoboxType);
        foreach (var el in extra)
            filter[el.Name] = el.Value;
        return filter;
    }

    [Description(
        """
            Search the Pages collection for entities by name.
            Matches the 'Titles' label in infobox.Data under the given infobox type.
            Returns id, name, continuity, and wikiUrl for each match.
            """
    )]
    public async Task<List<PageSummaryDto>> SearchByName(
        [Description(InfoboxTypeParamDescription)] string infoboxType,
        [Description("Entity name to match (case-insensitive regex), e.g. 'Luke Skywalker', 'Battle of Yavin'")] string name,
        [Description(ContinuityParamDescription)] string? continuity = null,
        [Description("Max results (default 5)")] int limit = 5
    )
    {
        var titleMatch = new BsonDocument(
            "$elemMatch",
            new BsonDocument { { InfoboxBsonFields.Label, InfoboxFieldLabels.Titles }, { InfoboxBsonFields.Values, new BsonDocument("$regex", new BsonRegularExpression(EscapeRegex(name), "i")) } }
        );

        var filter = WithTemplate(infoboxType, new BsonDocument(PageBsonFields.InfoboxData, titleMatch));

        if (continuity is not null)
            filter[PageBsonFields.Continuity] = continuity;

        var docs = await Pages
            .Find(filter)
            .Limit(limit)
            .Project(
                new BsonDocument
                {
                    { MongoFields.Id, 1 },
                    { PageBsonFields.Continuity, 1 },
                    { PageBsonFields.WikiUrl, 1 },
                    {
                        InfoboxBsonFields.Data,
                        new BsonDocument(
                            "$filter",
                            new BsonDocument
                            {
                                { "input", "$" + PageBsonFields.InfoboxData },
                                { "as", "d" },
                                { "cond", new BsonDocument("$eq", new BsonArray { "$$d.Label", InfoboxFieldLabels.Titles }) },
                            }
                        )
                    },
                }
            )
            .ToListAsync();

        return docs.Select(d => new PageSummaryDto(
                Id: d[MongoFields.Id].AsInt32,
                Name: d[InfoboxBsonFields.Data].AsBsonArray.FirstOrDefault()?[InfoboxBsonFields.Values].AsBsonArray.FirstOrDefault()?.AsString ?? "",
                Continuity: d.Contains(PageBsonFields.Continuity) ? d[PageBsonFields.Continuity].AsString : "",
                WikiUrl: d.Contains(PageBsonFields.WikiUrl) ? d[PageBsonFields.WikiUrl].AsString : ""
            ))
            .ToList();
    }

    [Description(
        """
            Get the full infobox data for a specific page by its integer PageId.
            Returns all infobox.Data labels and values for the entity.
            """
    )]
    public async Task<PageDetailDto?> GetPageById([Description(InfoboxTypeParamDescription)] string infoboxType, [Description("The integer _id (PageId) of the page")] int id)
    {
        var filter = WithTemplate(infoboxType, new BsonDocument(MongoFields.Id, id));
        var doc = await Pages.Find(filter).FirstOrDefaultAsync();
        if (doc == null)
            return null;

        var infobox = doc[PageBsonFields.Infobox].AsBsonDocument;
        var data = infobox[InfoboxBsonFields.Data]
            .AsBsonArray.OfType<BsonDocument>()
            .Select(d => new InfoboxDataRowDto(Label: d[InfoboxBsonFields.Label].AsString, Values: d[InfoboxBsonFields.Values].AsBsonArray.Select(v => v.AsString).ToList()))
            .ToList();

        return new PageDetailDto(
            Id: doc[MongoFields.Id].AsInt32,
            Continuity: doc.Contains(PageBsonFields.Continuity) ? doc[PageBsonFields.Continuity].AsString : "",
            WikiUrl: doc.Contains(PageBsonFields.WikiUrl) ? doc[PageBsonFields.WikiUrl].AsString : "",
            Data: data
        );
    }

    [Description(
        """
            Get distinct values and counts for a specific infobox.Data label across pages of a given type.
            Use to explore what values exist for a field before writing aggregations.
            """
    )]
    public async Task<List<ValueCountDto>> SampleLabelValues(
        [Description(InfoboxTypeParamDescription)] string infoboxType,
        [Description("The infobox.Data label to sample, e.g. Alignment, Area, Born, Died, Homeworld, Origin")] string label,
        [Description(ContinuityParamDescription)] string? continuity = null
    )
    {
        var matchDoc = TemplateFilter(infoboxType);
        matchDoc[PageBsonFields.InfoboxDataLabel] = label;
        if (continuity is not null)
            matchDoc[PageBsonFields.Continuity] = continuity;

        var pipeline = new[]
        {
            new BsonDocument("$match", matchDoc),
            new BsonDocument("$unwind", "$" + PageBsonFields.InfoboxData),
            new BsonDocument("$match", new BsonDocument(PageBsonFields.InfoboxDataLabel, label)),
            new BsonDocument("$unwind", "$" + PageBsonFields.InfoboxDataValues),
            new BsonDocument("$group", new BsonDocument { { MongoFields.Id, "$" + PageBsonFields.InfoboxDataValues }, { "count", new BsonDocument("$sum", 1) } }),
            new BsonDocument("$sort", new BsonDocument("count", -1)),
            new BsonDocument("$limit", 30),
        };

        var results = await Pages.Aggregate<BsonDocument>(pipeline).ToListAsync();

        return results.Select(d => new ValueCountDto(Value: d[MongoFields.Id].IsBsonNull ? "(null)" : d[MongoFields.Id].AsString, Count: d["count"].AsInt32)).ToList();
    }

    [Description(
        """
            Search pages where a specific infobox.Data label contains a matching value.
            Example: find Character pages with Homeworld containing 'Tatooine', or Battle pages
            with Place containing 'Yavin'.
            """
    )]
    public async Task<List<PageMatchDto>> SearchByProperty(
        [Description(InfoboxTypeParamDescription)] string infoboxType,
        [Description("infobox.Data label to filter on, e.g. Homeworld, Affiliation(s), Place, Outcome, Origin, Species")] string label,
        [Description("Value to match (case-insensitive regex), e.g. 'Tatooine', 'Rebel Alliance', 'Corellia'")] string value,
        [Description(ContinuityParamDescription)] string? continuity = null,
        [Description("Max results (default 10)")] int limit = 10
    )
    {
        var filter = WithTemplate(
            infoboxType,
            new BsonDocument(
                PageBsonFields.InfoboxData,
                new BsonDocument(
                    "$elemMatch",
                    new BsonDocument { { InfoboxBsonFields.Label, label }, { InfoboxBsonFields.Values, new BsonDocument("$regex", new BsonRegularExpression(EscapeRegex(value), "i")) } }
                )
            )
        );
        if (continuity is not null)
            filter[PageBsonFields.Continuity] = continuity;

        var docs = await Pages
            .Find(filter)
            .Limit(limit)
            .Project(
                new BsonDocument
                {
                    { MongoFields.Id, 1 },
                    { PageBsonFields.Continuity, 1 },
                    { PageBsonFields.WikiUrl, 1 },
                    {
                        InfoboxBsonFields.Data,
                        new BsonDocument(
                            "$filter",
                            new BsonDocument
                            {
                                { "input", "$" + PageBsonFields.InfoboxData },
                                { "as", "d" },
                                {
                                    "cond",
                                    new BsonDocument(
                                        "$in",
                                        new BsonArray
                                        {
                                            "$$d.Label",
                                            new BsonArray { InfoboxFieldLabels.Titles, label },
                                        }
                                    )
                                },
                            }
                        )
                    },
                }
            )
            .ToListAsync();

        return docs.Select(d =>
            {
                var data = d[InfoboxBsonFields.Data].AsBsonArray.OfType<BsonDocument>().ToList();
                return new PageMatchDto(
                    Id: d[MongoFields.Id].AsInt32,
                    Name: data.FirstOrDefault(x => x[InfoboxBsonFields.Label].AsString == InfoboxFieldLabels.Titles)?[InfoboxBsonFields.Values].AsBsonArray.FirstOrDefault()?.AsString ?? "",
                    MatchValue: data.FirstOrDefault(x => x[InfoboxBsonFields.Label].AsString == label)?[InfoboxBsonFields.Values].AsBsonArray.Select(v => v.AsString).ToList(),
                    Continuity: d.Contains(PageBsonFields.Continuity) ? d[PageBsonFields.Continuity].AsString : "",
                    WikiUrl: d.Contains(PageBsonFields.WikiUrl) ? d[PageBsonFields.WikiUrl].AsString : ""
                );
            })
            .ToList();
    }

    [Description(
        """
            Get the values of a specific infobox.Data label for a page you already know the id of.
            Example: get 'Affiliation(s)' or 'Children' values for a character with a known PageId.
            """
    )]
    public async Task<List<string>> GetPropertyValues(
        [Description(InfoboxTypeParamDescription)] string infoboxType,
        [Description("The integer _id (PageId) of the page")] int id,
        [Description("infobox.Data label to retrieve, e.g. 'Affiliation(s)', 'Children', 'Parent(s)', 'Homeworld', 'Outcome'")] string label
    )
    {
        var filter = WithTemplate(infoboxType, new BsonDocument(MongoFields.Id, id));
        var doc = await Pages
            .Find(filter)
            .Project(
                new BsonDocument(
                    InfoboxBsonFields.Data,
                    new BsonDocument(
                        "$filter",
                        new BsonDocument
                        {
                            { "input", "$" + PageBsonFields.InfoboxData },
                            { "as", "d" },
                            { "cond", new BsonDocument("$eq", new BsonArray { "$$d.Label", label }) },
                        }
                    )
                )
            )
            .FirstOrDefaultAsync();

        if (doc == null)
            return [];

        return doc[InfoboxBsonFields.Data].AsBsonArray.OfType<BsonDocument>().SelectMany(d => d[InfoboxBsonFields.Values].AsBsonArray.Select(v => v.AsString)).ToList();
    }

    [Description(
        """
            Search pages whose temporal infobox.Data labels contain a specific date string.
            Works with any temporal field: Born, Died, Date, Beginning, End, Date established,
            Date dissolved, Date reorganized, Constructed, Destroyed, Release date, etc.
            Matches partial strings so '19 BBY' also matches '19 BBY, Polis Massa'.
            For structured temporal queries, prefer find_entities_by_year with semantic filters.
            """
    )]
    public async Task<List<PageDateMatchDto>> SearchByDate(
        [Description(InfoboxTypeParamDescription)] string infoboxType,
        [Description("BBY/ABY date string to search for, e.g. '19 BBY', '4 ABY', '0 BBY'")] string date,
        [Description(
            """
                Which infobox.Data label to search. Common values: 'Born', 'Died', 'Date', 'Beginning', 'End',
                'Date established', 'Date dissolved', 'Date reorganized', 'Date restored', 'Date fragmented',
                'Constructed', 'Destroyed', 'Release date', 'Publication date', 'Air date'. Defaults to 'Date'.
                """
        )]
            string dateLabel = "Date",
        [Description(ContinuityParamDescription)] string? continuity = null,
        [Description("Max results (default 10)")] int limit = 10
    )
    {
        var filter = WithTemplate(
            infoboxType,
            new BsonDocument(
                PageBsonFields.InfoboxData,
                new BsonDocument(
                    "$elemMatch",
                    new BsonDocument { { InfoboxBsonFields.Label, dateLabel }, { InfoboxBsonFields.Values, new BsonDocument("$regex", new BsonRegularExpression(EscapeRegex(date), "i")) } }
                )
            )
        );
        if (continuity is not null)
            filter[PageBsonFields.Continuity] = continuity;

        var docs = await Pages
            .Find(filter)
            .Limit(limit)
            .Project(
                new BsonDocument
                {
                    { MongoFields.Id, 1 },
                    { PageBsonFields.Continuity, 1 },
                    { PageBsonFields.WikiUrl, 1 },
                    {
                        InfoboxBsonFields.Data,
                        new BsonDocument(
                            "$filter",
                            new BsonDocument
                            {
                                { "input", "$" + PageBsonFields.InfoboxData },
                                { "as", "d" },
                                {
                                    "cond",
                                    new BsonDocument(
                                        "$in",
                                        new BsonArray
                                        {
                                            "$$d.Label",
                                            new BsonArray { InfoboxFieldLabels.Titles, dateLabel },
                                        }
                                    )
                                },
                            }
                        )
                    },
                }
            )
            .ToListAsync();

        return docs.Select(d =>
            {
                var data = d[InfoboxBsonFields.Data].AsBsonArray.OfType<BsonDocument>().ToList();
                return new PageDateMatchDto(
                    Id: d[MongoFields.Id].AsInt32,
                    Name: data.FirstOrDefault(x => x[InfoboxBsonFields.Label].AsString == InfoboxFieldLabels.Titles)?[InfoboxBsonFields.Values].AsBsonArray.FirstOrDefault()?.AsString ?? "",
                    Date: data.FirstOrDefault(x => x[InfoboxBsonFields.Label].AsString == dateLabel)?[InfoboxBsonFields.Values].AsBsonArray.Select(v => v.AsString).ToList(),
                    Continuity: d.Contains(PageBsonFields.Continuity) ? d[PageBsonFields.Continuity].AsString : "",
                    WikiUrl: d.Contains(PageBsonFields.WikiUrl) ? d[PageBsonFields.WikiUrl].AsString : ""
                );
            })
            .ToList();
    }

    [Description(
        """
            Search pages that reference a given entity by its wikiUrl in their infobox.Data links.
            Use for cross-references — e.g. find all Battle pages that link to a specific Character's wikiUrl.
            """
    )]
    public async Task<List<PageSummaryDto>> SearchByLink(
        [Description(InfoboxTypeParamDescription)] string infoboxType,
        [Description("wikiUrl of the entity to find references to, e.g. 'https://starwars.fandom.com/wiki/Luke_Skywalker'")] string wikiUrl,
        [Description(ContinuityParamDescription)] string? continuity = null,
        [Description("Max results (default 10)")] int limit = 10
    )
    {
        var filter = WithTemplate(
            infoboxType,
            new BsonDocument(
                PageBsonFields.InfoboxData,
                new BsonDocument(
                    "$elemMatch",
                    new BsonDocument
                    {
                        {
                            InfoboxBsonFields.Links,
                            new BsonDocument("$elemMatch", new BsonDocument { { InfoboxBsonFields.Href, new BsonDocument("$regex", new BsonRegularExpression(EscapeRegex(wikiUrl), "i")) } })
                        },
                    }
                )
            )
        );
        if (continuity is not null)
            filter[PageBsonFields.Continuity] = continuity;

        var docs = await Pages
            .Find(filter)
            .Limit(limit)
            .Project(
                new BsonDocument
                {
                    { MongoFields.Id, 1 },
                    { PageBsonFields.Continuity, 1 },
                    { PageBsonFields.WikiUrl, 1 },
                    {
                        InfoboxBsonFields.Data,
                        new BsonDocument(
                            "$filter",
                            new BsonDocument
                            {
                                { "input", "$" + PageBsonFields.InfoboxData },
                                { "as", "d" },
                                { "cond", new BsonDocument("$eq", new BsonArray { "$$d.Label", InfoboxFieldLabels.Titles }) },
                            }
                        )
                    },
                }
            )
            .ToListAsync();

        return docs.Select(d => new PageSummaryDto(
                Id: d[MongoFields.Id].AsInt32,
                Name: d[InfoboxBsonFields.Data].AsBsonArray.OfType<BsonDocument>().FirstOrDefault()?[InfoboxBsonFields.Values].AsBsonArray.FirstOrDefault()?.AsString ?? "",
                Continuity: d.Contains(PageBsonFields.Continuity) ? d[PageBsonFields.Continuity].AsString : "",
                WikiUrl: d.Contains(PageBsonFields.WikiUrl) ? d[PageBsonFields.WikiUrl].AsString : ""
            ))
            .ToList();
    }

    [Description(
        """
            Discover which infobox labels contain links (relationships to other pages) for a given entity type.
            Returns label names ranked by how many pages have links under that label.
            Use before render_graph to discover available relationship labels instead of guessing.
            If pageId is provided, returns only labels with links for that specific entity.
            """
    )]
    public async Task<object> SampleLinkLabels(
        [Description(InfoboxTypeParamDescription)] string infoboxType,
        [Description("Optional specific page _id to inspect. If provided, returns only labels with links on that page.")] int? pageId = null,
        [Description(ContinuityParamDescription)] string? continuity = null
    )
    {
        if (pageId.HasValue)
        {
            // Single-entity mode: return labels with links for this specific page
            var filter = WithTemplate(infoboxType, new BsonDocument(MongoFields.Id, pageId.Value));
            var doc = await Pages.Find(filter).FirstOrDefaultAsync();
            if (doc == null)
                return new List<LinkLabelDetailDto>();

            var infobox = doc[PageBsonFields.Infobox].AsBsonDocument;
            return infobox[InfoboxBsonFields.Data]
                .AsBsonArray.OfType<BsonDocument>()
                .Where(d => d.Contains(InfoboxBsonFields.Links) && d[InfoboxBsonFields.Links].AsBsonArray.Count > 0)
                .Select(d => new LinkLabelDetailDto(
                    Label: d[InfoboxBsonFields.Label].AsString,
                    LinkCount: d[InfoboxBsonFields.Links].AsBsonArray.Count,
                    SampleLinks: d[InfoboxBsonFields.Links]
                        .AsBsonArray.OfType<BsonDocument>()
                        .Take(3)
                        .Select(l => l.Contains("Text") ? l["Text"].AsString : "")
                        .Where(t => !string.IsNullOrWhiteSpace(t))
                        .ToList()
                ))
                .OrderByDescending(x => x.LinkCount)
                .ToList();
        }

        // Type-level aggregation: find labels with links across all pages of this type
        var matchDoc = TemplateFilter(infoboxType);
        if (continuity is not null)
            matchDoc[PageBsonFields.Continuity] = continuity;

        var pipeline = new[]
        {
            new BsonDocument("$match", matchDoc),
            new BsonDocument("$unwind", "$" + PageBsonFields.InfoboxData),
            new BsonDocument("$match", new BsonDocument(PageBsonFields.InfoboxDataLinks, new BsonDocument("$exists", true))),
            new BsonDocument("$match", new BsonDocument(PageBsonFields.InfoboxDataLinks, new BsonDocument("$not", new BsonDocument("$size", 0)))),
            new BsonDocument("$group", new BsonDocument { { MongoFields.Id, "$" + PageBsonFields.InfoboxDataLabel }, { "pageCount", new BsonDocument("$sum", 1) } }),
            new BsonDocument("$sort", new BsonDocument("pageCount", -1)),
            new BsonDocument("$limit", 50),
        };

        var results = await Pages.Aggregate<BsonDocument>(pipeline).ToListAsync();

        return results.Select(d => new LabelPageCountDto(Label: d[MongoFields.Id].IsBsonNull ? "(null)" : d[MongoFields.Id].AsString, PageCount: d["pageCount"].AsInt32)).ToList();
    }

    [Description(
        """
            List all distinct infobox type names from the Pages collection.
            Use this to discover valid infoboxType values for other tools
            (e.g. Character, Battle, Species, Food, Droid).
            """
    )]
    public async Task<List<string>> ListInfoboxTypes()
    {
        var pipeline = new[]
        {
            new BsonDocument("$match", new BsonDocument(PageBsonFields.Infobox, new BsonDocument("$ne", BsonNull.Value))),
            new BsonDocument("$group", new BsonDocument(MongoFields.Id, "$" + PageBsonFields.InfoboxTemplate)),
        };
        var cursor = await Pages.Aggregate<BsonDocument>(pipeline).ToListAsync();
        return cursor
            .Select(d => d[MongoFields.Id].IsBsonNull ? null : d[MongoFields.Id].AsString)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => RecordService.SanitizeTemplateName(n!))
            .Where(n => n != "Unknown")
            .Distinct()
            .OrderBy(x => x)
            .ToList();
    }

    [Description(
        """
            List all timeline event category names.
            Each category corresponds to a timeline collection (e.g. 'Battle', 'Character', 'War').
            Call before render_timeline to discover valid category names.
            """
    )]
    public async Task<List<string>> ListTimelineCategories()
    {
        var db = mongoClient.GetDatabase(settings.Value.DatabaseName);
        var cursor = await db.ListCollectionNamesAsync();
        var allNames = await cursor.ToListAsync();
        return allNames.Where(n => n.StartsWith(Collections.TimelinePrefix)).Select(n => n[Collections.TimelinePrefix.Length..]).OrderBy(x => x).ToList();
    }

    public IReadOnlyList<AITool> AsAIFunctions() =>
        [
            AIFunctionFactory.Create(SearchByName, "search_pages_by_name"),
            AIFunctionFactory.Create(GetPageById, "get_page_by_id"),
            AIFunctionFactory.Create(GetPropertyValues, "get_page_property"),
            AIFunctionFactory.Create(SearchByProperty, "search_pages_by_property"),
            AIFunctionFactory.Create(SearchByDate, "search_pages_by_date"),
            AIFunctionFactory.Create(SearchByLink, "search_pages_by_link"),
            AIFunctionFactory.Create(SampleLabelValues, "sample_property_values"),
            AIFunctionFactory.Create(SampleLinkLabels, "sample_link_labels"),
            AIFunctionFactory.Create(ListInfoboxTypes, "list_infobox_types"),
            AIFunctionFactory.Create(ListTimelineCategories, "list_timeline_categories"),
        ];
}
