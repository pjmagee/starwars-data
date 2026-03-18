using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries;
using TimelineEvent = StarWarsData.Models.Entities.TimelineEvent;

namespace StarWarsData.Services;

public class TimelineService
{
    readonly ILogger<TimelineService> _logger;
    readonly TemplateHelper _templateHelper;
    readonly IMongoDatabase _timelineEventsDb;
    readonly IMongoClient _mongoClient;

    public TimelineService(
        ILogger<TimelineService> logger,
        IOptions<SettingsOptions> settingsOptions,
        IMongoClient mongoClient,
        TemplateHelper templateHelper
    )
    {
        _logger = logger;
        _templateHelper = templateHelper;
        _mongoClient = mongoClient;
        _timelineEventsDb = mongoClient.GetDatabase(settingsOptions.Value.TimelineEventsDb);
    }

    public async Task<GroupedTimelineResult> GetTimelineEvents(
        IList<string> templates,
        Continuity? continuity = null,
        Universe? universe = null,
        int page = 1,
        int pageSize = 20,
        float? yearFrom = null,
        Demarcation? yearFromDemarcation = null,
        float? yearTo = null,
        Demarcation? yearToDemarcation = null,
        string? search = null
    )
    {
        // Get all available collections from timeline-events db
        var availableCollections = await GetTimelineCategories();

        // If templates are specified, filter collections; otherwise use all
        var collectionsToQuery = templates.Any()
            ? availableCollections.Where(c => templates.Contains(c)).ToList()
            : availableCollections;

        if (!collectionsToQuery.Any())
            return new GroupedTimelineResult
            {
                Total = 0,
                Size = pageSize,
                Page = page,
                Items = [],
            };

        // Build a BSON match filter
        var matchConditions = new BsonArray();

        // Continuity filter (stored as int in timeline events DB, no BsonRepresentation string override)
        if (continuity != null && continuity != Continuity.Both)
        {
            matchConditions.Add(
                new BsonDocument(
                    "Continuity",
                    new BsonDocument(
                        "$in",
                        new BsonArray
                        {
                            (int)continuity.Value,
                            (int)Continuity.Both,
                            (int)Continuity.Unknown,
                        }
                    )
                )
            );
        }

        // Universe filter
        if (universe != null)
        {
            matchConditions.Add(
                new BsonDocument(
                    "Universe",
                    new BsonDocument(
                        "$in",
                        new BsonArray { (int)universe.Value, (int)Universe.Unknown }
                    )
                )
            );
        }

        // Year range filter
        if (
            yearFrom.HasValue
            && yearFromDemarcation.HasValue
            && yearTo.HasValue
            && yearToDemarcation.HasValue
        )
        {
            var linearFrom = ToLinearYear(yearFrom.Value, yearFromDemarcation.Value);
            var linearTo = ToLinearYear(yearTo.Value, yearToDemarcation.Value);
            if (linearFrom > linearTo)
                (linearFrom, linearTo) = (linearTo, linearFrom);

            var linearYearExpr = new BsonDocument(
                "$cond",
                new BsonArray
                {
                    new BsonDocument("$eq", new BsonArray { "$Demarcation", "Bby" }),
                    new BsonDocument("$multiply", new BsonArray { "$Year", -1 }),
                    "$Year",
                }
            );

            matchConditions.Add(
                new BsonDocument(
                    "$expr",
                    new BsonDocument(
                        "$and",
                        new BsonArray
                        {
                            new BsonDocument("$ne", new BsonArray { "$Year", BsonNull.Value }),
                            new BsonDocument("$gte", new BsonArray { linearYearExpr, linearFrom }),
                            new BsonDocument("$lte", new BsonArray { linearYearExpr, linearTo }),
                        }
                    )
                )
            );
        }

        // Search filter
        if (!string.IsNullOrWhiteSpace(search))
        {
            matchConditions.Add(
                new BsonDocument(
                    "Title",
                    new BsonDocument("$regex", new BsonRegularExpression(search, "i"))
                )
            );
        }

        var matchStage = new BsonDocument(
            "$match",
            matchConditions.Count == 1
                ? matchConditions[0].AsBsonDocument
                : new BsonDocument("$and", matchConditions)
        );

        // Use the first collection as the base, $unionWith the rest
        var baseCollectionName = collectionsToQuery[0];
        var baseCollection = _timelineEventsDb.GetCollection<BsonDocument>(baseCollectionName);

        var pipeline = new List<BsonDocument>();

        // Union all other collections into the base
        for (int i = 1; i < collectionsToQuery.Count; i++)
        {
            pipeline.Add(new BsonDocument("$unionWith", collectionsToQuery[i]));
        }

        // Match (filter)
        pipeline.Add(matchStage);

        // Sort: Demarcation ascending (ABY before BBY alphabetically — but we need BBY first)
        // Use linear year for correct chronological order
        pipeline.Add(
            new BsonDocument(
                "$addFields",
                new BsonDocument(
                    "_linearYear",
                    new BsonDocument(
                        "$cond",
                        new BsonArray
                        {
                            new BsonDocument("$eq", new BsonArray { "$Demarcation", "Bby" }),
                            new BsonDocument("$multiply", new BsonArray { "$Year", -1 }),
                            "$Year",
                        }
                    )
                )
            )
        );

        pipeline.Add(new BsonDocument("$sort", new BsonDocument("_linearYear", 1)));

        // Count total with a $facet: one branch for total count, one for paginated data
        pipeline.Add(
            new BsonDocument(
                "$facet",
                new BsonDocument
                {
                    {
                        "total",
                        new BsonArray { new BsonDocument("$count", "count") }
                    },
                    {
                        "data",
                        new BsonArray
                        {
                            new BsonDocument("$skip", (page - 1) * pageSize),
                            new BsonDocument("$limit", pageSize),
                            new BsonDocument("$project", new BsonDocument("_linearYear", 0)),
                        }
                    },
                }
            )
        );

        var result = await baseCollection
            .Aggregate<BsonDocument>(pipeline.ToArray())
            .FirstOrDefaultAsync();

        if (result == null)
            return new GroupedTimelineResult
            {
                Total = 0,
                Size = pageSize,
                Page = page,
                Items = [],
            };

        var totalCount =
            result["total"].AsBsonArray.Count > 0
                ? result["total"].AsBsonArray[0].AsBsonDocument["count"].AsInt32
                : 0;

        var pagedEvents = result["data"]
            .AsBsonArray.Select(doc =>
                BsonSerializer.Deserialize<TimelineEvent>(doc.AsBsonDocument)
            )
            .ToList();

        // Group by year
        var groupedByYear = pagedEvents
            .GroupBy(x => x.DisplayYear)
            .Select(x => new GroupedTimelines { Events = x.ToList(), Year = x.Key });

        return new GroupedTimelineResult
        {
            Total = totalCount,
            Size = pageSize,
            Page = page,
            Items = groupedByYear,
        };
    }

    public async Task<List<string>> GetDistinctTemplatesAsync()
    {
        // Get all collection names from timeline-events db, these represent the templates/categories
        var collections = await GetTimelineCategories();
        return collections
            .Select(_templateHelper.GetTemplateFromUri)
            .Distinct()
            .OrderBy(x => x)
            .ToList();
    }

    public async Task<GroupedTimelineResult> GetCategoryTimelineEvents(
        string category,
        Continuity? continuity = null,
        Universe? universe = null,
        int page = 1,
        int pageSize = 20
    )
    {
        var categoryCollection = _timelineEventsDb.GetCollection<TimelineEvent>(category);

        // Build combined filter
        var continuityFilter = BuildContinuityFilter(continuity);
        var universeFilter = BuildUniverseFilter(universe);
        var combinedFilter = Builders<TimelineEvent>.Filter.And(continuityFilter, universeFilter);

        var sort = Builders<TimelineEvent>
            .Sort.Ascending(x => x.Demarcation)
            .Ascending(x => x.Year);

        var timelineEventDocuments = await categoryCollection
            .Find(combinedFilter)
            .Sort(sort)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync();

        var timelineEvents = timelineEventDocuments
            .Select(doc => new TimelineEvent
            {
                Title = doc.Title,
                TemplateUri = doc.Template,
                ImageUrl = doc.ImageUrl,
                Demarcation = doc.Demarcation,
                Year = doc.Year,
                Properties = doc.Properties,
                DateEvent = doc.DateEvent,
                Continuity = doc.Continuity,
            })
            .ToList();

        timelineEvents.Sort();

        var groupedByYear = timelineEvents
            .GroupBy(x => x.DisplayYear)
            .Select(x => new GroupedTimelines { Events = x.ToList(), Year = x.Key });

        var total = await categoryCollection.CountDocumentsAsync(combinedFilter);

        return new GroupedTimelineResult
        {
            Total = (int)total,
            Size = pageSize,
            Page = page,
            Items = groupedByYear,
        };
    }

    private static FilterDefinition<TimelineEvent> BuildContinuityFilter(Continuity? continuity)
    {
        if (continuity == null || continuity == Continuity.Both)
        {
            // Show both Canon and Legends content
            return Builders<TimelineEvent>.Filter.In(
                x => x.Continuity,
                new[] { Continuity.Canon, Continuity.Legends, Continuity.Both }
            );
        }

        // Filter by specific continuity
        return Builders<TimelineEvent>.Filter.Eq(x => x.Continuity, continuity.Value);
    }

    private static FilterDefinition<TimelineEvent> BuildUniverseFilter(Universe? universe)
    {
        if (universe == null)
        {
            // No filter — return all content
            return Builders<TimelineEvent>.Filter.Empty;
        }

        // Include Unknown documents alongside the requested universe,
        // since most timeline events don't have Universe explicitly set.
        return Builders<TimelineEvent>.Filter.In(
            x => x.Universe,
            new[] { universe.Value, Universe.Unknown }
        );
    }

    public async Task<List<string>> GetTimelineCategories()
    {
        var names = await _timelineEventsDb.ListCollectionNamesAsync();
        List<string> results = await names.ToListAsync();
        return results.OrderBy(x => x).ToList();
    }

    /// <summary>
    /// Converts a year + demarcation into a linear value where BBY is negative and ABY is positive.
    /// e.g. 19 BBY = -19, 4 ABY = 4, 0 BBY = 0
    /// </summary>
    static double ToLinearYear(float year, Demarcation demarcation) =>
        demarcation == Demarcation.Bby ? -year : year;

    /// <summary>
    /// Builds a MongoDB filter that matches timeline events within a year range.
    /// Uses the $expr operator to compute a linear year from Demarcation and Year fields.
    /// </summary>
    static FilterDefinition<TimelineEvent> BuildYearRangeFilter(
        float fromYear,
        Demarcation fromDemarcation,
        float toYear,
        Demarcation toDemarcation
    )
    {
        var linearFrom = ToLinearYear(fromYear, fromDemarcation);
        var linearTo = ToLinearYear(toYear, toDemarcation);

        // Ensure from <= to on the linear scale
        if (linearFrom > linearTo)
            (linearFrom, linearTo) = (linearTo, linearFrom);

        // Build an $expr that computes a linear year:
        // if Demarcation == "Bby" then -Year else Year
        // then checks linearFrom <= linearYear <= linearTo
        var linearYearExpr = new BsonDocument(
            "$cond",
            new BsonArray
            {
                new BsonDocument("$eq", new BsonArray { "$Demarcation", "Bby" }),
                new BsonDocument("$multiply", new BsonArray { "$Year", -1 }),
                "$Year",
            }
        );

        var expr = new BsonDocument(
            "$expr",
            new BsonDocument(
                "$and",
                new BsonArray
                {
                    new BsonDocument("$ne", new BsonArray { "$Year", BsonNull.Value }),
                    new BsonDocument("$gte", new BsonArray { linearYearExpr, linearFrom }),
                    new BsonDocument("$lte", new BsonArray { linearYearExpr, linearTo }),
                }
            )
        );

        return new BsonDocumentFilterDefinition<TimelineEvent>(expr);
    }
}
