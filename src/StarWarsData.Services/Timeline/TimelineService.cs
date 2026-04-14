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

    public TimelineService(ILogger<TimelineService> logger, IOptions<SettingsOptions> settingsOptions, IMongoClient mongoClient, TemplateHelper templateHelper)
    {
        _logger = logger;
        _templateHelper = templateHelper;
        _mongoClient = mongoClient;
        _timelineEventsDb = mongoClient.GetDatabase(settingsOptions.Value.DatabaseName);
    }

    public async Task<GroupedTimelineResult> GetTimelineEvents(
        IList<string> templates,
        Continuity? continuity = null,
        Realm? realm = null,
        int page = 1,
        int pageSize = 20,
        float? yearFrom = null,
        Demarcation? yearFromDemarcation = null,
        float? yearTo = null,
        Demarcation? yearToDemarcation = null,
        string? search = null,
        Calendar? calendar = null
    )
    {
        // Get all available collections from timeline-events db
        var availableCollections = await GetTimelineCategories();

        // If templates are specified, filter collections; otherwise use all
        var collectionsToQuery = templates.Any() ? availableCollections.Where(c => templates.Contains(c)).ToList() : availableCollections;

        if (!collectionsToQuery.Any())
            return new GroupedTimelineResult
            {
                Total = 0,
                Size = pageSize,
                Page = page,
                Items = [],
            };

        // Build a BSON match filter.
        // Enums are stored as strings across this app via the global
        // EnumRepresentationConvention(BsonType.String) registered in Program.cs
        // — filter values must be the enum names ("Canon", "Starwars", "Bby"),
        // not the underlying ints.
        var matchConditions = new BsonArray();

        // Continuity filter
        if (continuity != null && continuity != Continuity.Both)
        {
            matchConditions.Add(
                new BsonDocument(TimelineEventBsonFields.Continuity, new BsonDocument("$in", new BsonArray { continuity.Value.ToString(), Continuity.Both.ToString(), Continuity.Unknown.ToString() }))
            );
        }

        // Realm filter
        if (realm != null)
        {
            matchConditions.Add(new BsonDocument(TimelineEventBsonFields.Realm, new BsonDocument("$in", new BsonArray { realm.Value.ToString(), Realm.Unknown.ToString() })));
        }

        // Calendar filter. Documents written before this field existed default to Galactic
        // at read time via the $in check on the missing/Galactic branch.
        if (calendar != null)
        {
            var targetCalendar = calendar.Value.ToString();
            if (calendar == Calendar.Galactic)
            {
                // Match explicit Galactic AND legacy rows missing the Calendar field.
                matchConditions.Add(
                    new BsonDocument(
                        "$or",
                        new BsonArray { new BsonDocument(TimelineEventBsonFields.Calendar, targetCalendar), new BsonDocument(TimelineEventBsonFields.Calendar, new BsonDocument("$exists", false)) }
                    )
                );
            }
            else
            {
                matchConditions.Add(new BsonDocument(TimelineEventBsonFields.Calendar, targetCalendar));
            }
        }

        // Year range filter. When BBY/ABY demarcations are supplied we assume a galactic
        // range query; when only numeric bounds are supplied we treat them as signed CE
        // years for the real calendar. The two branches never mix: a galactic range skips
        // real rows (they have no $Year) and a real range skips galactic rows (no $RealYear).
        if (yearFrom.HasValue && yearTo.HasValue)
        {
            if (yearFromDemarcation.HasValue && yearToDemarcation.HasValue)
            {
                var linearFrom = ToLinearYear(yearFrom.Value, yearFromDemarcation.Value);
                var linearTo = ToLinearYear(yearTo.Value, yearToDemarcation.Value);
                if (linearFrom > linearTo)
                    (linearFrom, linearTo) = (linearTo, linearFrom);

                var linearYearExpr = new BsonDocument(
                    "$cond",
                    new BsonArray { new BsonDocument("$eq", new BsonArray { "$Demarcation", "Bby" }), new BsonDocument("$multiply", new BsonArray { "$Year", -1 }), "$Year" }
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
            else
            {
                // Real-calendar range — straight $gte/$lte on RealYear (signed CE).
                var from = (int)Math.Round(Math.Min(yearFrom.Value, yearTo.Value));
                var to = (int)Math.Round(Math.Max(yearFrom.Value, yearTo.Value));
                matchConditions.Add(new BsonDocument(TimelineEventBsonFields.RealYear, new BsonDocument { { "$gte", from }, { "$lte", to } }));
            }
        }

        // Search filter
        if (!string.IsNullOrWhiteSpace(search))
        {
            matchConditions.Add(new BsonDocument("Title", new BsonDocument("$regex", MongoSafe.Regex(search))));
        }

        var matchFilter = matchConditions.Count switch
        {
            0 => new BsonDocument(),
            1 => matchConditions[0].AsBsonDocument,
            _ => new BsonDocument("$and", matchConditions),
        };
        var matchStage = new BsonDocument("$match", matchFilter);

        // Use the first collection as the base, $unionWith the rest
        // collectionsToQuery has bare names (e.g. "Battle"); add prefix for actual MongoDB collection
        var baseCollectionName = Collections.TimelinePrefix + collectionsToQuery[0];
        var baseCollection = _timelineEventsDb.GetCollection<BsonDocument>(baseCollectionName);

        var pipeline = new List<BsonDocument>();

        // Union all other collections into the base
        for (int i = 1; i < collectionsToQuery.Count; i++)
        {
            pipeline.Add(new BsonDocument("$unionWith", Collections.TimelinePrefix + collectionsToQuery[i]));
        }

        // Match (filter)
        pipeline.Add(matchStage);

        // Sort chronologically. Real-calendar events use $RealYear directly (signed CE);
        // galactic events compute -Year for BBY, +Year for ABY. The $switch picks the
        // correct axis per document so mixed result sets still sort in a single pass.
        pipeline.Add(
            new BsonDocument(
                "$addFields",
                new BsonDocument(
                    "_linearYear",
                    new BsonDocument(
                        "$switch",
                        new BsonDocument
                        {
                            {
                                "branches",
                                new BsonArray
                                {
                                    new BsonDocument { { "case", new BsonDocument("$eq", new BsonArray { "$Calendar", "Real" }) }, { "then", "$RealYear" } },
                                    new BsonDocument
                                    {
                                        { "case", new BsonDocument("$eq", new BsonArray { "$Demarcation", "Bby" }) },
                                        { "then", new BsonDocument("$multiply", new BsonArray { "$Year", -1 }) },
                                    },
                                }
                            },
                            { "default", "$Year" },
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
                        new BsonArray { new BsonDocument("$skip", (page - 1) * pageSize), new BsonDocument("$limit", pageSize), new BsonDocument("$project", new BsonDocument("_linearYear", 0)) }
                    },
                }
            )
        );

        var result = await baseCollection.Aggregate<BsonDocument>(pipeline.ToArray()).FirstOrDefaultAsync();

        if (result == null)
            return new GroupedTimelineResult
            {
                Total = 0,
                Size = pageSize,
                Page = page,
                Items = [],
            };

        var totalCount = result["total"].AsBsonArray.Count > 0 ? result["total"].AsBsonArray[0].AsBsonDocument["count"].AsInt32 : 0;

        var pagedEvents = result["data"].AsBsonArray.Select(doc => BsonSerializer.Deserialize<TimelineEvent>(doc.AsBsonDocument)).ToList();

        // Group by year
        var groupedByYear = pagedEvents.GroupBy(x => x.DisplayYear).Select(x => new GroupedTimelines { Events = x.ToList(), Year = x.Key });

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
        return collections.Select(_templateHelper.GetTemplateFromUri).Distinct().OrderBy(x => x).ToList();
    }

    public async Task<GroupedTimelineResult> GetCategoryTimelineEvents(string category, Continuity? continuity = null, Realm? realm = null, int page = 1, int pageSize = 20)
    {
        var categoryCollection = _timelineEventsDb.GetCollection<TimelineEvent>(Collections.TimelinePrefix + category);

        // Build combined filter
        var continuityFilter = BuildContinuityFilter(continuity);
        var realmFilter = BuildRealmFilter(realm);
        var combinedFilter = Builders<TimelineEvent>.Filter.And(continuityFilter, realmFilter);

        var sort = Builders<TimelineEvent>.Sort.Ascending(x => x.Demarcation).Ascending(x => x.Year);

        var timelineEventDocuments = await categoryCollection.Find(combinedFilter).Sort(sort).Skip((page - 1) * pageSize).Limit(pageSize).ToListAsync();

        var timelineEvents = timelineEventDocuments
            .Select(doc => new TimelineEvent
            {
                Title = doc.Title,
                Template = doc.Template,
                TemplateUri = doc.TemplateUri,
                ImageUrl = doc.ImageUrl,
                Demarcation = doc.Demarcation,
                Year = doc.Year,
                Calendar = doc.Calendar,
                RealYear = doc.RealYear,
                Properties = doc.Properties,
                DateEvent = doc.DateEvent,
                Continuity = doc.Continuity,
                Realm = doc.Realm,
                PageId = doc.PageId,
                WikiUrl = doc.WikiUrl,
            })
            .ToList();

        timelineEvents.Sort();

        var groupedByYear = timelineEvents.GroupBy(x => x.DisplayYear).Select(x => new GroupedTimelines { Events = x.ToList(), Year = x.Key });

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
            return Builders<TimelineEvent>.Filter.In(x => x.Continuity, [Continuity.Canon, Continuity.Legends, Continuity.Both]);
        }

        // Filter by specific continuity
        return Builders<TimelineEvent>.Filter.Eq(x => x.Continuity, continuity.Value);
    }

    private static FilterDefinition<TimelineEvent> BuildRealmFilter(Realm? realm)
    {
        if (realm == null)
        {
            // No filter — return all content
            return Builders<TimelineEvent>.Filter.Empty;
        }

        // Include Unknown documents alongside the requested realm,
        // since most timeline events don't have Realm explicitly set.
        return Builders<TimelineEvent>.Filter.In(x => x.Realm, [realm.Value, Realm.Unknown]);
    }

    public async Task<List<string>> GetTimelineCategories()
    {
        var names = await _timelineEventsDb.ListCollectionNamesAsync();
        var allNames = await names.ToListAsync();
        return allNames.Where(n => n.StartsWith(Collections.TimelinePrefix)).Select(n => n[Collections.TimelinePrefix.Length..]).OrderBy(x => x).ToList();
    }

    /// <summary>
    /// Load eras from the Era collection in the timeline events database.
    /// Groups documents by title to build start/end year ranges.
    /// When <paramref name="continuity"/> is set to Canon or Legends, only eras
    /// matching that continuity (plus Both/Unknown as shared) are returned.
    /// </summary>
    public async Task<List<Era>> GetErasAsync(Continuity? continuity = null, CancellationToken ct = default)
    {
        var collectionNames = await _timelineEventsDb.ListCollectionNamesAsync(cancellationToken: ct);
        if (!collectionNames.ToList().Contains(Collections.TimelinePrefix + KgNodeTypes.Era))
            return [];

        var eraCollection = _timelineEventsDb.GetCollection<TimelineEvent>(Collections.TimelinePrefix + KgNodeTypes.Era);

        // Strongly-typed filter — routes through the serializer and correctly
        // matches the string representation stored via EnumRepresentationConvention.
        var filter = Builders<TimelineEvent>.Filter.Ne(e => e.Year, null);
        if (continuity is not null && continuity != Continuity.Both)
        {
            filter &= Builders<TimelineEvent>.Filter.In(e => e.Continuity, [continuity.Value, Continuity.Both, Continuity.Unknown]);
        }

        var docs = await eraCollection.Find(filter).ToListAsync(ct);

        var grouped = docs.GroupBy(e => StripLegendsSuffix(e.Title ?? "")).Where(g => !string.IsNullOrWhiteSpace(g.Key));

        var eras = new List<Era>();
        foreach (var group in grouped)
        {
            var entries = group.Select(e => (year: e.Year!.Value, dem: e.Demarcation)).OrderBy(e => e.dem == Demarcation.Bby ? -e.year : e.year).ToList();

            var start = entries.First();
            var end = entries.Last();

            eras.Add(
                new Era
                {
                    Name = group.Key,
                    StartYear = start.year,
                    StartDemarcation = start.dem,
                    EndYear = end.year,
                    EndDemarcation = end.dem,
                }
            );
        }

        // Sort chronologically
        eras.Sort((a, b) => ToLinearYear(a.StartYear, a.StartDemarcation).CompareTo(ToLinearYear(b.StartYear, b.StartDemarcation)));

        return eras;
    }

    static string StripLegendsSuffix(string title) => title.EndsWith("/Legends", StringComparison.OrdinalIgnoreCase) ? title[..^"/Legends".Length] : title;

    /// <summary>
    /// Converts a year + demarcation into a linear value where BBY is negative and ABY is positive.
    /// e.g. 19 BBY = -19, 4 ABY = 4, 0 BBY = 0
    /// </summary>
    static double ToLinearYear(float year, Demarcation demarcation) => demarcation == Demarcation.Bby ? -year : year;

    /// <summary>
    /// Builds a MongoDB filter that matches timeline events within a year range.
    /// Uses the $expr operator to compute a linear year from Demarcation and Year fields.
    /// </summary>
    static FilterDefinition<TimelineEvent> BuildYearRangeFilter(float fromYear, Demarcation fromDemarcation, float toYear, Demarcation toDemarcation)
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
            new BsonArray { new BsonDocument("$eq", new BsonArray { "$Demarcation", "Bby" }), new BsonDocument("$multiply", new BsonArray { "$Year", -1 }), "$Year" }
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

    /// <summary>
    /// Look up a knowledge-graph node's temporal anchor for the /timeline/{nodeId} route.
    /// Returns null when the node has no usable range (StartYear/EndYear both null).
    /// See Design-014 Phase 2.
    /// </summary>
    public async Task<NodeAnchor?> GetNodeAnchorAsync(int nodeId, CancellationToken ct = default)
    {
        var nodes = _timelineEventsDb.GetCollection<GraphNode>(Collections.KgNodes);
        var node = await nodes.Find(n => n.PageId == nodeId).FirstOrDefaultAsync(ct);
        if (node is null)
            return null;

        var dimensions = BuildAnchorDimensions(node);
        if (dimensions.Count == 0)
            return null;

        return new NodeAnchor
        {
            Id = node.PageId,
            Name = node.Name,
            Type = node.Type,
            Continuity = node.Continuity,
            ImageUrl = node.ImageUrl,
            WikiUrl = node.WikiUrl,
            Dimensions = dimensions,
            DefaultDimension = dimensions[0].Key,
        };
    }

    /// <summary>
    /// v1: emits a single "default" dimension from node.StartYear/EndYear, labelled per node type.
    /// If only one of the two is known (e.g. government with "Founded" but no "Dissolved"),
    /// the missing bound falls back to the other — yielding a single-year window rather than
    /// a 404. Data-extraction gaps are common in the infobox ETL; surfacing the node is
    /// more useful than hiding it. Multi-dimension breakdown is Phase 2.1 — see Design-014.
    /// </summary>
    static List<NodeAnchorDimension> BuildAnchorDimensions(GraphNode node)
    {
        if (node.StartYear is null && node.EndYear is null)
            return [];

        var fromYear = node.StartYear ?? node.EndYear!.Value;
        var toYear = node.EndYear ?? node.StartYear!.Value;

        var fromAbs = Math.Abs(fromYear);
        var fromDem = fromYear < 0 ? Demarcation.Bby : Demarcation.Aby;
        var toAbs = Math.Abs(toYear);
        var toDem = toYear < 0 ? Demarcation.Bby : Demarcation.Aby;

        return
        [
            new NodeAnchorDimension
            {
                Key = "default",
                Label = LabelForType(node.Type),
                YearFrom = fromAbs,
                YearTo = toAbs,
                FromDemarcation = fromDem,
                ToDemarcation = toDem,
                FromText = node.StartDateText,
                ToText = node.EndDateText,
            },
        ];
    }

    static string LabelForType(string type) =>
        type switch
        {
            "Conflict" or "Battle" or "War" or "Campaign" or "Mission" => "Hostilities",
            "Government" or "Polity" or "Empire" => "Reign",
            "Organization" or "Organisation" or "Company" => "Active period",
            "Character" => "Lifetime",
            "Treaty" or "Law" or "Election" => "In effect",
            _ => "Lifecycle",
        };
}
