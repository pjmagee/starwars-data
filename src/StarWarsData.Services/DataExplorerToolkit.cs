using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using MongoDB.Bson;
using MongoDB.Driver;

namespace StarWarsData.Services;

public class DataExplorerToolkit(IMongoClient mongoClient)
{
    const string Database = "starwars-extracted-infoboxes";

    [Description(
        "Find documents in any infobox collection by matching the 'Titles' label in their Data array. "
            + "Use this to look up any named entity (character, battle, species, planet, etc.) by name. "
            + "Returns id, name, continuity, and wikiUrl for each match."
    )]
    public async Task<string> FindByTitle(
        [Description(
            "The infobox collection to search, e.g. Character, Battle, Species, CelestialBody, War"
        )]
            string collection,
        [Description("The name to search for, e.g. 'Luke Skywalker', 'Battle of Yavin'")]
            string name,
        [Description("Optional continuity filter: 'Canon', 'Legends', or omit for all")]
            string? continuity = null,
        [Description("Max results to return, default 5")] int limit = 5
    )
    {
        var col = mongoClient.GetDatabase(Database).GetCollection<BsonDocument>(collection);

        var titleMatch = new BsonDocument(
            "$elemMatch",
            new BsonDocument
            {
                { "Label", "Titles" },
                { "Values", new BsonDocument("$regex", new BsonRegularExpression(name, "i")) },
            }
        );

        var filter = new BsonDocument("Data", titleMatch);
        if (continuity is not null)
            filter["Continuity"] = continuity;

        var docs = await col.Find(filter)
            .Limit(limit)
            .Project(
                new BsonDocument
                {
                    { "_id", 1 },
                    { "Continuity", 1 },
                    { "WikiUrl", 1 },
                    {
                        "Data",
                        new BsonDocument(
                            "$filter",
                            new BsonDocument
                            {
                                { "input", "$Data" },
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
            continuity = d.Contains("Continuity") ? d["Continuity"].AsString : "",
            wikiUrl = d.Contains("WikiUrl") ? d["WikiUrl"].AsString : "",
        });

        return JsonSerializer.Serialize(results);
    }

    [Description(
        "Get all Data label values for a specific document by its integer id. "
            + "Use this to inspect the full infobox of a known entity."
    )]
    public async Task<string> GetById(
        [Description("The infobox collection, e.g. Character, Battle, Species")] string collection,
        [Description("The integer _id (PageId) of the document")] int id
    )
    {
        var col = mongoClient.GetDatabase(Database).GetCollection<BsonDocument>(collection);
        var doc = await col.Find(new BsonDocument("_id", id)).FirstOrDefaultAsync();
        if (doc == null)
            return "{}";

        var data = doc["Data"]
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
                continuity = doc.Contains("Continuity") ? doc["Continuity"].AsString : "",
                wikiUrl = doc.Contains("WikiUrl") ? doc["WikiUrl"].AsString : "",
                data,
            }
        );
    }

    [Description(
        "Returns distinct values and counts for a given Data.Label in a collection. "
            + "Call this before aggregating on any Data field so you can see the real value patterns."
    )]
    public async Task<string> SampleLabelValues(
        [Description(
            "The infobox collection to query, e.g. ForcePower, Character, Battle, Species"
        )]
            string collection,
        [Description("The Data.Label to sample, e.g. Alignment, Area, Born, Died, Homeworld")]
            string label,
        [Description("Optional Continuity filter: 'Canon', 'Legends', or omit for all")]
            string? continuity = null
    )
    {
        var col = mongoClient.GetDatabase(Database).GetCollection<BsonDocument>(collection);

        var matchStage = new BsonDocument(
            "$match",
            continuity is not null
                ? new BsonDocument { { "Continuity", continuity }, { "Data.Label", label } }
                : new BsonDocument("Data.Label", label)
        );

        var pipeline = new[]
        {
            matchStage,
            new BsonDocument("$unwind", "$Data"),
            new BsonDocument("$match", new BsonDocument("Data.Label", label)),
            new BsonDocument("$unwind", "$Data.Values"),
            new BsonDocument(
                "$group",
                new BsonDocument
                {
                    { "_id", "$Data.Values" },
                    { "count", new BsonDocument("$sum", 1) },
                }
            ),
            new BsonDocument("$sort", new BsonDocument("count", -1)),
            new BsonDocument("$limit", 30),
        };

        var results = await col.Aggregate<BsonDocument>(pipeline).ToListAsync();

        var values = results.Select(d => new
        {
            value = d["_id"].IsBsonNull ? "(null)" : d["_id"].AsString,
            count = d["count"].AsInt32,
        });

        return JsonSerializer.Serialize(values);
    }

    [Description(
        "Find documents in a collection where a specific Data label contains a matching value. "
            + "Useful for filtering by Homeworld, Affiliation, Species, Outcome, Place, Type, etc. "
            + "Example: find all Characters with Homeworld='Tatooine', or Battles with Place='Yavin'."
    )]
    public async Task<string> FindByLabelValue(
        [Description(
            "The infobox collection, e.g. Character, Battle, CelestialBody, StarshipClass"
        )]
            string collection,
        [Description(
            "The Data.Label to filter on, e.g. Homeworld, Affiliation(s), Place, Outcome, Type, Species"
        )]
            string label,
        [Description(
            "The value to match (case-insensitive regex), e.g. 'Tatooine', 'Rebel Alliance', 'Victory'"
        )]
            string value,
        [Description("Optional continuity filter: 'Canon', 'Legends', or omit for all")]
            string? continuity = null,
        [Description("Max results to return, default 10")] int limit = 10
    )
    {
        var col = mongoClient.GetDatabase(Database).GetCollection<BsonDocument>(collection);

        var filter = new BsonDocument(
            "Data",
            new BsonDocument(
                "$elemMatch",
                new BsonDocument
                {
                    { "Label", label },
                    { "Values", new BsonDocument("$regex", new BsonRegularExpression(value, "i")) },
                }
            )
        );
        if (continuity is not null)
            filter["Continuity"] = continuity;

        var docs = await col.Find(filter)
            .Limit(limit)
            .Project(
                new BsonDocument
                {
                    { "_id", 1 },
                    { "Continuity", 1 },
                    { "WikiUrl", 1 },
                    {
                        "Data",
                        new BsonDocument(
                            "$filter",
                            new BsonDocument
                            {
                                { "input", "$Data" },
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
                continuity = d.Contains("Continuity") ? d["Continuity"].AsString : "",
                wikiUrl = d.Contains("WikiUrl") ? d["WikiUrl"].AsString : "",
            };
        });

        return JsonSerializer.Serialize(results);
    }

    [Description(
        "Get the values of a specific Data label for a known document. "
            + "Use this to read a single field from a document you already have the id for. "
            + "Example: get the 'Affiliation(s)' or 'Children' values for a character id."
    )]
    public async Task<string> GetLabelValues(
        [Description("The infobox collection, e.g. Character, Battle, Species")] string collection,
        [Description("The integer _id (PageId) of the document")] int id,
        [Description(
            "The Data.Label to retrieve, e.g. 'Affiliation(s)', 'Children', 'Parent(s)', 'Homeworld', 'Outcome'"
        )]
            string label
    )
    {
        var col = mongoClient.GetDatabase(Database).GetCollection<BsonDocument>(collection);
        var doc = await col.Find(new BsonDocument("_id", id))
            .Project(
                new BsonDocument(
                    "Data",
                    new BsonDocument(
                        "$filter",
                        new BsonDocument
                        {
                            { "input", "$Data" },
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
        "Find documents in a collection whose Born, Died, or Date label contains a specific BBY/ABY year. "
            + "Use this to find characters born/died in a specific year, or battles/events on a specific date. "
            + "Examples: date='19 BBY', date='4 ABY', date='0 BBY'. Matches partial strings so '19 BBY' matches '19 BBY, Polis Massa'."
    )]
    public async Task<string> FindByDate(
        [Description("The infobox collection, e.g. Character, Battle, Event, War, Duel, Campaign")]
            string collection,
        [Description("The BBY/ABY date string to search for, e.g. '19 BBY', '4 ABY', '0 BBY'")]
            string date,
        [Description("Which date label to search: 'Born', 'Died', or 'Date'. Defaults to 'Date'")]
            string dateLabel = "Date",
        [Description("Optional continuity filter: 'Canon', 'Legends', or omit for all")]
            string? continuity = null,
        [Description("Max results to return, default 10")] int limit = 10
    )
    {
        var col = mongoClient.GetDatabase(Database).GetCollection<BsonDocument>(collection);

        var filter = new BsonDocument(
            "Data",
            new BsonDocument(
                "$elemMatch",
                new BsonDocument
                {
                    { "Label", dateLabel },
                    { "Values", new BsonDocument("$regex", new BsonRegularExpression(date, "i")) },
                }
            )
        );
        if (continuity is not null)
            filter["Continuity"] = continuity;

        var docs = await col.Find(filter)
            .Limit(limit)
            .Project(
                new BsonDocument
                {
                    { "_id", 1 },
                    { "Continuity", 1 },
                    { "WikiUrl", 1 },
                    {
                        "Data",
                        new BsonDocument(
                            "$filter",
                            new BsonDocument
                            {
                                { "input", "$Data" },
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
                continuity = d.Contains("Continuity") ? d["Continuity"].AsString : "",
                wikiUrl = d.Contains("WikiUrl") ? d["WikiUrl"].AsString : "",
            };
        });

        return JsonSerializer.Serialize(results);
    }

    [Description(
        "Find documents in any collection that reference another entity by its WikiUrl in their Data links. "
            + "Use this to find relationships — e.g. all Battles where a Character participated, "
            + "or all Characters affiliated with an Organization."
    )]
    public async Task<string> FindRelated(
        [Description(
            "The collection to search for references, e.g. Battle, Character, Organization, Event"
        )]
            string collection,
        [Description(
            "The WikiUrl of the entity to find references to, e.g. 'https://starwars.fandom.com/wiki/Luke_Skywalker/Legends'"
        )]
            string wikiUrl,
        [Description("Optional continuity filter: 'Canon', 'Legends', or omit for all")]
            string? continuity = null,
        [Description("Max results to return, default 10")] int limit = 10
    )
    {
        var col = mongoClient.GetDatabase(Database).GetCollection<BsonDocument>(collection);

        var filter = new BsonDocument(
            "Data",
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
                                        new BsonRegularExpression(wikiUrl, "i")
                                    )
                                },
                            }
                        )
                    },
                }
            )
        );
        if (continuity is not null)
            filter["Continuity"] = continuity;

        var docs = await col.Find(filter)
            .Limit(limit)
            .Project(
                new BsonDocument
                {
                    { "_id", 1 },
                    { "Continuity", 1 },
                    { "WikiUrl", 1 },
                    {
                        "Data",
                        new BsonDocument(
                            "$filter",
                            new BsonDocument
                            {
                                { "input", "$Data" },
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
            continuity = d.Contains("Continuity") ? d["Continuity"].AsString : "",
            wikiUrl = d.Contains("WikiUrl") ? d["WikiUrl"].AsString : "",
        });

        return JsonSerializer.Serialize(results);
    }

    public IReadOnlyList<AITool> AsAIFunctions() =>
        [
            AIFunctionFactory.Create(FindByTitle, "find_by_title"),
            AIFunctionFactory.Create(GetById, "get_by_id"),
            AIFunctionFactory.Create(GetLabelValues, "get_label_values"),
            AIFunctionFactory.Create(FindByLabelValue, "find_by_label_value"),
            AIFunctionFactory.Create(FindByDate, "find_by_date"),
            AIFunctionFactory.Create(FindRelated, "find_related"),
            AIFunctionFactory.Create(SampleLabelValues, "sample_label_values"),
        ];
}
