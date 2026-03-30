using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;

namespace StarWarsData.Services;

/// <summary>
/// AI tools for querying the Pages collection.
/// All data lives in a single "Pages" collection. Each page may have an embedded "infobox" object.
/// The infobox type (Character, Battle, Species, etc.) is determined by the infobox.Template field.
/// These tools filter Pages by infobox type automatically using a regex on infobox.Template.
/// </summary>
public class DataExplorerToolkit(IMongoClient mongoClient, IOptions<SettingsOptions> settings)
{
    IMongoCollection<BsonDocument> Pages =>
        mongoClient.GetDatabase(settings.Value.DatabaseName).GetCollection<BsonDocument>(Collections.Pages);

    static string EscapeRegex(string input) => System.Text.RegularExpressions.Regex.Escape(input);

    /// <summary>
    /// Builds a regex filter on infobox.Template that matches the given type name at the end of the URL.
    /// e.g. "Character" matches "https://starwars.fandom.com/wiki/Template:Character"
    /// </summary>
    static BsonDocument TemplateFilter(string infoboxType) =>
        new(
            "infobox.Template",
            new BsonDocument("$regex", new BsonRegularExpression($":{infoboxType}$", "i"))
        );

    static BsonDocument WithTemplate(string infoboxType, BsonDocument extra)
    {
        var filter = TemplateFilter(infoboxType);
        foreach (var el in extra)
            filter[el.Name] = el.Value;
        return filter;
    }

    [Description(
        "Search the Pages collection for entities by name. "
            + "Queries the Pages collection, "
            + "filtering by infobox type (via infobox.Template regex) and matching the 'Titles' label in infobox.Data. "
            + "Returns id, name, continuity, and wikiUrl for each match."
    )]
    public async Task<string> SearchByName(
        [Description(
            "The infobox type name to filter by. This is NOT a MongoDB collection name — it filters Pages by infobox.Template regex. "
                + "Use list_infobox_types to discover valid values. Examples: Character, Battle, Species, CelestialBody, War, ForcePower, Food, Droid"
        )]
            string infoboxType,
        [Description(
            "The entity name to search for (case-insensitive regex), e.g. 'Luke Skywalker', 'Battle of Yavin'"
        )]
            string name,
        [Description("Optional continuity filter: 'Canon', 'Legends', or omit for all")]
            string? continuity = null,
        [Description("Max results to return, default 5")] int limit = 5
    )
    {
        var titleMatch = new BsonDocument(
            "$elemMatch",
            new BsonDocument
            {
                { "Label", "Titles" },
                {
                    "Values",
                    new BsonDocument("$regex", new BsonRegularExpression(EscapeRegex(name), "i"))
                },
            }
        );

        var filter = WithTemplate(infoboxType, new BsonDocument("infobox.Data", titleMatch));
        if (continuity is not null)
            filter["continuity"] = continuity;

        var docs = await Pages
            .Find(filter)
            .Limit(limit)
            .Project(
                new BsonDocument
                {
                    { "_id", 1 },
                    { "continuity", 1 },
                    { "wikiUrl", 1 },
                    {
                        "Data",
                        new BsonDocument(
                            "$filter",
                            new BsonDocument
                            {
                                { "input", "$infobox.Data" },
                                { "as", "d" },
                                {
                                    "cond",
                                    new BsonDocument("$eq", new BsonArray { "$$d.Label", "Titles" })
                                },
                            }
                        )
                    },
                }
            )
            .ToListAsync();

        var results = docs.Select(d => new
        {
            id = d["_id"].AsInt32,
            name = d["Data"]
                .AsBsonArray.FirstOrDefault()
                ?["Values"].AsBsonArray.FirstOrDefault()
                ?.AsString ?? "",
            continuity = d.Contains("continuity") ? d["continuity"].AsString : "",
            wikiUrl = d.Contains("wikiUrl") ? d["wikiUrl"].AsString : "",
        });

        return JsonSerializer.Serialize(results);
    }

    [Description(
        "Get the full infobox data for a specific page by its integer PageId (_id). "
            + "Queries the 'Pages' collection filtered by infobox type and _id. "
            + "Returns all infobox.Data labels and values for the entity."
    )]
    public async Task<string> GetPageById(
        [Description(
            "The infobox type name to filter by (e.g. Character, Battle, Species). "
                + "Filters Pages by infobox.Template regex — not a MongoDB collection name."
        )]
            string infoboxType,
        [Description("The integer _id (PageId) of the page")] int id
    )
    {
        var filter = WithTemplate(infoboxType, new BsonDocument("_id", id));
        var doc = await Pages.Find(filter).FirstOrDefaultAsync();
        if (doc == null)
            return "{}";

        var infobox = doc["infobox"].AsBsonDocument;
        var data = infobox["Data"]
            .AsBsonArray.OfType<BsonDocument>()
            .Select(d => new
            {
                label = d["Label"].AsString,
                values = d["Values"].AsBsonArray.Select(v => v.AsString).ToList(),
            });

        return JsonSerializer.Serialize(
            new
            {
                id = doc["_id"].AsInt32,
                continuity = doc.Contains("continuity") ? doc["continuity"].AsString : "",
                wikiUrl = doc.Contains("wikiUrl") ? doc["wikiUrl"].AsString : "",
                data,
            }
        );
    }

    [Description(
        "Get distinct values and counts for a specific infobox.Data label across pages of a given type. "
            + "Queries the 'Pages' collection, groups by infobox.Data.Values for the given label. "
            + "Use this to explore what values exist for a field before writing aggregations."
    )]
    public async Task<string> SampleLabelValues(
        [Description(
            "The infobox type name (e.g. ForcePower, Character, Battle, Species). "
                + "Filters Pages by infobox.Template regex — not a MongoDB collection name."
        )]
            string infoboxType,
        [Description(
            "The infobox.Data label to sample, e.g. Alignment, Area, Born, Died, Homeworld, Origin"
        )]
            string label,
        [Description("Optional continuity filter: 'Canon', 'Legends', or omit for all")]
            string? continuity = null
    )
    {
        var matchDoc = TemplateFilter(infoboxType);
        matchDoc["infobox.Data.Label"] = label;
        if (continuity is not null)
            matchDoc["continuity"] = continuity;

        var pipeline = new[]
        {
            new BsonDocument("$match", matchDoc),
            new BsonDocument("$unwind", "$infobox.Data"),
            new BsonDocument("$match", new BsonDocument("infobox.Data.Label", label)),
            new BsonDocument("$unwind", "$infobox.Data.Values"),
            new BsonDocument(
                "$group",
                new BsonDocument
                {
                    { "_id", "$infobox.Data.Values" },
                    { "count", new BsonDocument("$sum", 1) },
                }
            ),
            new BsonDocument("$sort", new BsonDocument("count", -1)),
            new BsonDocument("$limit", 30),
        };

        var results = await Pages.Aggregate<BsonDocument>(pipeline).ToListAsync();

        var values = results.Select(d => new
        {
            value = d["_id"].IsBsonNull ? "(null)" : d["_id"].AsString,
            count = d["count"].AsInt32,
        });

        return JsonSerializer.Serialize(values);
    }

    [Description(
        "Search pages where a specific infobox.Data label contains a matching value. "
            + "Queries the 'Pages' collection filtered by infobox type and an $elemMatch on infobox.Data. "
            + "Example: find all Character pages with Homeworld containing 'Tatooine', or Battle pages with Place containing 'Yavin'."
    )]
    public async Task<string> SearchByProperty(
        [Description(
            "The infobox type name (e.g. Character, Battle, CelestialBody, Food). "
                + "Filters Pages by infobox.Template regex — not a MongoDB collection name."
        )]
            string infoboxType,
        [Description(
            "The infobox.Data label to filter on, e.g. Homeworld, Affiliation(s), Place, Outcome, Origin, Species"
        )]
            string label,
        [Description(
            "The value to match (case-insensitive regex), e.g. 'Tatooine', 'Rebel Alliance', 'Corellia'"
        )]
            string value,
        [Description("Optional continuity filter: 'Canon', 'Legends', or omit for all")]
            string? continuity = null,
        [Description("Max results to return, default 10")] int limit = 10
    )
    {
        var filter = WithTemplate(
            infoboxType,
            new BsonDocument(
                "infobox.Data",
                new BsonDocument(
                    "$elemMatch",
                    new BsonDocument
                    {
                        { "Label", label },
                        {
                            "Values",
                            new BsonDocument(
                                "$regex",
                                new BsonRegularExpression(EscapeRegex(value), "i")
                            )
                        },
                    }
                )
            )
        );
        if (continuity is not null)
            filter["continuity"] = continuity;

        var docs = await Pages
            .Find(filter)
            .Limit(limit)
            .Project(
                new BsonDocument
                {
                    { "_id", 1 },
                    { "continuity", 1 },
                    { "wikiUrl", 1 },
                    {
                        "Data",
                        new BsonDocument(
                            "$filter",
                            new BsonDocument
                            {
                                { "input", "$infobox.Data" },
                                { "as", "d" },
                                {
                                    "cond",
                                    new BsonDocument(
                                        "$in",
                                        new BsonArray
                                        {
                                            "$$d.Label",
                                            new BsonArray { "Titles", label },
                                        }
                                    )
                                },
                            }
                        )
                    },
                }
            )
            .ToListAsync();

        var results = docs.Select(d =>
        {
            var data = d["Data"].AsBsonArray.OfType<BsonDocument>().ToList();
            return new
            {
                id = d["_id"].AsInt32,
                name = data.FirstOrDefault(x => x["Label"].AsString == "Titles")
                    ?["Values"].AsBsonArray.FirstOrDefault()
                    ?.AsString ?? "",
                matchValue = data.FirstOrDefault(x => x["Label"].AsString == label)
                    ?["Values"].AsBsonArray.Select(v => v.AsString)
                    .ToList(),
                continuity = d.Contains("continuity") ? d["continuity"].AsString : "",
                wikiUrl = d.Contains("wikiUrl") ? d["wikiUrl"].AsString : "",
            };
        });

        return JsonSerializer.Serialize(results);
    }

    [Description(
        "Get the values of a specific infobox.Data label for a page you already know the id of. "
            + "Queries the 'Pages' collection by _id and infobox type, returns just the requested label's values. "
            + "Example: get 'Affiliation(s)' or 'Children' values for a character with a known PageId."
    )]
    public async Task<string> GetPropertyValues(
        [Description(
            "The infobox type name (e.g. Character, Battle, Species). "
                + "Filters Pages by infobox.Template regex — not a MongoDB collection name."
        )]
            string infoboxType,
        [Description("The integer _id (PageId) of the page")] int id,
        [Description(
            "The infobox.Data label to retrieve, e.g. 'Affiliation(s)', 'Children', 'Parent(s)', 'Homeworld', 'Outcome'"
        )]
            string label
    )
    {
        var filter = WithTemplate(infoboxType, new BsonDocument("_id", id));
        var doc = await Pages
            .Find(filter)
            .Project(
                new BsonDocument(
                    "Data",
                    new BsonDocument(
                        "$filter",
                        new BsonDocument
                        {
                            { "input", "$infobox.Data" },
                            { "as", "d" },
                            {
                                "cond",
                                new BsonDocument("$eq", new BsonArray { "$$d.Label", label })
                            },
                        }
                    )
                )
            )
            .FirstOrDefaultAsync();

        if (doc == null)
            return "[]";

        var values = doc["Data"]
            .AsBsonArray.OfType<BsonDocument>()
            .SelectMany(d => d["Values"].AsBsonArray.Select(v => v.AsString))
            .ToList();

        return JsonSerializer.Serialize(values);
    }

    [Description(
        "Search pages whose Born, Died, or Date infobox.Data label contains a specific BBY/ABY year string. "
            + "Queries the 'Pages' collection filtered by infobox type and an $elemMatch on the date label. "
            + "Matches partial strings so '19 BBY' also matches '19 BBY, Polis Massa'."
    )]
    public async Task<string> SearchByDate(
        [Description(
            "The infobox type name (e.g. Character, Battle, Event, War, Duel, Campaign). "
                + "Filters Pages by infobox.Template regex — not a MongoDB collection name."
        )]
            string infoboxType,
        [Description("The BBY/ABY date string to search for, e.g. '19 BBY', '4 ABY', '0 BBY'")]
            string date,
        [Description(
            "Which infobox.Data label to search: 'Born', 'Died', or 'Date'. Defaults to 'Date'"
        )]
            string dateLabel = "Date",
        [Description("Optional continuity filter: 'Canon', 'Legends', or omit for all")]
            string? continuity = null,
        [Description("Max results to return, default 10")] int limit = 10
    )
    {
        var filter = WithTemplate(
            infoboxType,
            new BsonDocument(
                "infobox.Data",
                new BsonDocument(
                    "$elemMatch",
                    new BsonDocument
                    {
                        { "Label", dateLabel },
                        {
                            "Values",
                            new BsonDocument(
                                "$regex",
                                new BsonRegularExpression(EscapeRegex(date), "i")
                            )
                        },
                    }
                )
            )
        );
        if (continuity is not null)
            filter["continuity"] = continuity;

        var docs = await Pages
            .Find(filter)
            .Limit(limit)
            .Project(
                new BsonDocument
                {
                    { "_id", 1 },
                    { "continuity", 1 },
                    { "wikiUrl", 1 },
                    {
                        "Data",
                        new BsonDocument(
                            "$filter",
                            new BsonDocument
                            {
                                { "input", "$infobox.Data" },
                                { "as", "d" },
                                {
                                    "cond",
                                    new BsonDocument(
                                        "$in",
                                        new BsonArray
                                        {
                                            "$$d.Label",
                                            new BsonArray { "Titles", dateLabel },
                                        }
                                    )
                                },
                            }
                        )
                    },
                }
            )
            .ToListAsync();

        var results = docs.Select(d =>
        {
            var data = d["Data"].AsBsonArray.OfType<BsonDocument>().ToList();
            return new
            {
                id = d["_id"].AsInt32,
                name = data.FirstOrDefault(x => x["Label"].AsString == "Titles")
                    ?["Values"].AsBsonArray.FirstOrDefault()
                    ?.AsString ?? "",
                date = data.FirstOrDefault(x => x["Label"].AsString == dateLabel)
                    ?["Values"].AsBsonArray.Select(v => v.AsString)
                    .ToList(),
                continuity = d.Contains("continuity") ? d["continuity"].AsString : "",
                wikiUrl = d.Contains("wikiUrl") ? d["wikiUrl"].AsString : "",
            };
        });

        return JsonSerializer.Serialize(results);
    }

    [Description(
        "Search pages that reference a given entity by its wikiUrl in their infobox.Data links. "
            + "Queries the 'Pages' collection filtered by infobox type and an $elemMatch on infobox.Data.Links.Href. "
            + "Use this for cross-references — e.g. find all Battle pages that link to a specific Character's wikiUrl."
    )]
    public async Task<string> SearchByLink(
        [Description(
            "The infobox type name to search within (e.g. Battle, Character, Organization, Event). "
                + "Filters Pages by infobox.Template regex — not a MongoDB collection name."
        )]
            string infoboxType,
        [Description(
            "The wikiUrl of the entity to find references to, e.g. 'https://starwars.fandom.com/wiki/Luke_Skywalker'"
        )]
            string wikiUrl,
        [Description("Optional continuity filter: 'Canon', 'Legends', or omit for all")]
            string? continuity = null,
        [Description("Max results to return, default 10")] int limit = 10
    )
    {
        var filter = WithTemplate(
            infoboxType,
            new BsonDocument(
                "infobox.Data",
                new BsonDocument(
                    "$elemMatch",
                    new BsonDocument
                    {
                        {
                            "Links",
                            new BsonDocument(
                                "$elemMatch",
                                new BsonDocument
                                {
                                    {
                                        "Href",
                                        new BsonDocument(
                                            "$regex",
                                            new BsonRegularExpression(EscapeRegex(wikiUrl), "i")
                                        )
                                    },
                                }
                            )
                        },
                    }
                )
            )
        );
        if (continuity is not null)
            filter["continuity"] = continuity;

        var docs = await Pages
            .Find(filter)
            .Limit(limit)
            .Project(
                new BsonDocument
                {
                    { "_id", 1 },
                    { "continuity", 1 },
                    { "wikiUrl", 1 },
                    {
                        "Data",
                        new BsonDocument(
                            "$filter",
                            new BsonDocument
                            {
                                { "input", "$infobox.Data" },
                                { "as", "d" },
                                {
                                    "cond",
                                    new BsonDocument("$eq", new BsonArray { "$$d.Label", "Titles" })
                                },
                            }
                        )
                    },
                }
            )
            .ToListAsync();

        var results = docs.Select(d => new
        {
            id = d["_id"].AsInt32,
            name = d["Data"]
                .AsBsonArray.OfType<BsonDocument>()
                .FirstOrDefault()
                ?["Values"].AsBsonArray.FirstOrDefault()
                ?.AsString ?? "",
            continuity = d.Contains("continuity") ? d["continuity"].AsString : "",
            wikiUrl = d.Contains("wikiUrl") ? d["wikiUrl"].AsString : "",
        });

        return JsonSerializer.Serialize(results);
    }

    [Description(
        "Discover which infobox labels contain links (relationships to other pages) for a given entity type. "
            + "Returns label names ranked by how many pages have links under that label. "
            + "Use this BEFORE render_graph to discover which relationship labels are available, "
            + "instead of guessing label names. The LLM should then decide which labels map to "
            + "upLabels (ancestors), downLabels (descendants), or peerLabels (same-level). "
            + "If pageId is provided, returns only labels with links for that specific entity."
    )]
    public async Task<string> SampleLinkLabels(
        [Description(
            "The infobox type name (e.g. Character, Battle, Organization, Species). "
                + "Filters Pages by infobox.Template regex — not a MongoDB collection name."
        )]
            string infoboxType,
        [Description(
            "Optional: specific page _id to inspect. If provided, returns only labels with links on that page."
        )]
            int? pageId = null,
        [Description("Optional continuity filter: 'Canon', 'Legends', or omit for all")]
            string? continuity = null
    )
    {
        if (pageId.HasValue)
        {
            // Single-entity mode: return labels with links for this specific page
            var filter = WithTemplate(infoboxType, new BsonDocument("_id", pageId.Value));
            var doc = await Pages.Find(filter).FirstOrDefaultAsync();
            if (doc == null)
                return "[]";

            var infobox = doc["infobox"].AsBsonDocument;
            var labels = infobox["Data"]
                .AsBsonArray.OfType<BsonDocument>()
                .Where(d => d.Contains("Links") && d["Links"].AsBsonArray.Count > 0)
                .Select(d => new
                {
                    label = d["Label"].AsString,
                    linkCount = d["Links"].AsBsonArray.Count,
                    sampleLinks = d["Links"]
                        .AsBsonArray.OfType<BsonDocument>()
                        .Take(3)
                        .Select(l => l.Contains("Text") ? l["Text"].AsString : "")
                        .Where(t => !string.IsNullOrWhiteSpace(t))
                        .ToList(),
                })
                .OrderByDescending(x => x.linkCount)
                .ToList();

            return JsonSerializer.Serialize(labels);
        }

        // Type-level aggregation: find labels with links across all pages of this type
        var matchDoc = TemplateFilter(infoboxType);
        if (continuity is not null)
            matchDoc["continuity"] = continuity;

        var pipeline = new[]
        {
            new BsonDocument("$match", matchDoc),
            new BsonDocument("$unwind", "$infobox.Data"),
            new BsonDocument(
                "$match",
                new BsonDocument("infobox.Data.Links", new BsonDocument("$exists", true))
            ),
            new BsonDocument(
                "$match",
                new BsonDocument(
                    "infobox.Data.Links",
                    new BsonDocument("$not", new BsonDocument("$size", 0))
                )
            ),
            new BsonDocument(
                "$group",
                new BsonDocument
                {
                    { "_id", "$infobox.Data.Label" },
                    { "pageCount", new BsonDocument("$sum", 1) },
                }
            ),
            new BsonDocument("$sort", new BsonDocument("pageCount", -1)),
            new BsonDocument("$limit", 50),
        };

        var results = await Pages.Aggregate<BsonDocument>(pipeline).ToListAsync();

        var values = results.Select(d => new
        {
            label = d["_id"].IsBsonNull ? "(null)" : d["_id"].AsString,
            pageCount = d["pageCount"].AsInt32,
        });

        return JsonSerializer.Serialize(values);
    }

    [Description(
        "List all distinct infobox type names from the Pages collection. "
            + "Aggregates distinct infobox.Template values from the Pages collection and returns sanitized names. "
            + "Use this to discover what infoboxType values are valid for other tools (e.g. Character, Battle, Species, Food, Droid)."
    )]
    public async Task<string> ListInfoboxTypes()
    {
        var pipeline = new[]
        {
            new BsonDocument(
                "$match",
                new BsonDocument("infobox", new BsonDocument("$ne", BsonNull.Value))
            ),
            new BsonDocument("$group", new BsonDocument("_id", "$infobox.Template")),
        };
        var cursor = await Pages.Aggregate<BsonDocument>(pipeline).ToListAsync();
        var names = cursor
            .Select(d => d["_id"].IsBsonNull ? null : d["_id"].AsString)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => RecordService.SanitizeTemplateName(n!))
            .Where(n => n != "Unknown")
            .Distinct()
            .OrderBy(x => x)
            .ToList();
        return JsonSerializer.Serialize(names);
    }

    [Description(
        "List all timeline event category names. "
            + "Each category corresponds to a timeline collection (e.g. 'Battle', 'Character', 'War'). "
            + "Use this before render_timeline to discover valid category names."
    )]
    public async Task<string> ListTimelineCategories()
    {
        var db = mongoClient.GetDatabase(settings.Value.DatabaseName);
        // Timeline collections use a prefix; list all that match
        var cursor = await db.ListCollectionNamesAsync();
        var allNames = await cursor.ToListAsync();
        var timelineNames = allNames
            .Where(n => n.StartsWith(Collections.TimelinePrefix))
            .Select(n => n[Collections.TimelinePrefix.Length..])
            .OrderBy(x => x)
            .ToList();
        return JsonSerializer.Serialize(timelineNames);
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
