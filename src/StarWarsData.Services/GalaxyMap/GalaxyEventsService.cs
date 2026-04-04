using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries;

namespace StarWarsData.Services;

public class GalaxyEventsService
{
    /// <summary>
    /// Property labels that indicate a geographic location reference.
    /// Used to discover which collections are mappable and to extract place names.
    /// </summary>
    static readonly string[] LocationLabels =
    [
        "Place",
        "Location",
        "System",
        "Headquarters",
        "Capital",
        "Grid square",
        "Celestial body",
    ];

    /// <summary>
    /// Collections that are metadata / non-event and should be skipped even if they have years.
    /// </summary>
    static readonly HashSet<string> SkipCollections = new(StringComparer.OrdinalIgnoreCase)
    {
        "system_profile",
        "system.profile",
    };

    readonly ILogger<GalaxyEventsService> _logger;
    readonly IMongoDatabase _timelineDb;
    readonly IMongoCollection<Page> _pages;

    public GalaxyEventsService(
        ILogger<GalaxyEventsService> logger,
        IOptions<SettingsOptions> settingsOptions,
        IMongoClient mongoClient
    )
    {
        _logger = logger;
        var db = mongoClient.GetDatabase(settingsOptions.Value.DatabaseName);
        _timelineDb = db;
        _pages = db.GetCollection<Page>(Collections.Pages);
    }

    /// <summary>
    /// Build continuity + universe filters for timeline event queries.
    /// Continuity is stored as int in the timeline events DB.
    /// </summary>
    static FilterDefinition<TimelineEvent> BuildGlobalFilter(
        Continuity? continuity,
        Universe? universe
    )
    {
        var filters = new List<FilterDefinition<TimelineEvent>>();

        if (continuity is not null && continuity != Continuity.Both)
        {
            // Include events marked as the selected continuity, Both, or Unknown
            filters.Add(
                Builders<TimelineEvent>.Filter.In(
                    e => e.Continuity,
                    [continuity.Value, Continuity.Both, Continuity.Unknown]
                )
            );
        }

        if (universe is not null)
        {
            filters.Add(
                Builders<TimelineEvent>.Filter.In(
                    e => e.Universe,
                    [universe.Value, Universe.Unknown]
                )
            );
        }

        return filters.Count > 0
            ? Builders<TimelineEvent>.Filter.And(filters)
            : Builders<TimelineEvent>.Filter.Empty;
    }

    /// <summary>
    /// Discover which timeline collections have both Year data AND location-relevant properties.
    /// Returns the collection names and their counts.
    /// </summary>
    async Task<List<LensSummary>> DiscoverLensesAsync(
        Continuity? continuity = null,
        CancellationToken ct = default
    )
    {
        var allNames = await _timelineDb.ListCollectionNamesAsync(cancellationToken: ct);
        var collectionNames = allNames
            .ToList()
            .Where(n => n.StartsWith(Collections.TimelinePrefix) && !SkipCollections.Contains(n))
            .OrderBy(n => n)
            .ToList();

        var results = new List<LensSummary>();

        // Build a raw BsonDocument continuity filter (Continuity stored as int)
        BsonDocument? continuityFilter = null;
        if (continuity is not null && continuity != Continuity.Both)
        {
            continuityFilter = new BsonDocument(
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
            );
        }

        foreach (var name in collectionNames)
        {
            var shortName = name[Collections.TimelinePrefix.Length..];
            var collection = _timelineDb.GetCollection<BsonDocument>(name);

            // Check if collection has Year data (respecting continuity)
            var yearFilter = new BsonDocument("Year", new BsonDocument("$ne", BsonNull.Value));
            var baseFilter = continuityFilter is not null
                ? new BsonDocument("$and", new BsonArray { continuityFilter, yearFilter })
                : yearFilter;

            var withYear = await collection.CountDocumentsAsync(baseFilter, cancellationToken: ct);

            if (withYear == 0)
                continue;

            // Check if collection has any location-relevant properties
            var locationFilter = new BsonDocument(
                "Properties.Label",
                new BsonDocument("$in", new BsonArray(LocationLabels))
            );
            var withLocationFilter = continuityFilter is not null
                ? new BsonDocument(
                    "$and",
                    new BsonArray { continuityFilter, yearFilter, locationFilter }
                )
                : new BsonDocument("$and", new BsonArray { yearFilter, locationFilter });
            var withLocation = await collection.CountDocumentsAsync(
                withLocationFilter,
                cancellationToken: ct
            );

            if (withLocation == 0)
                continue;

            results.Add(
                new LensSummary
                {
                    Lens = shortName,
                    TotalCount = (int)withYear,
                    WithLocationCount = (int)withLocation,
                }
            );
        }

        results.Sort((a, b) => b.TotalCount.CompareTo(a.TotalCount));
        return results;
    }

    /// <summary>
    /// Load eras from the Era timeline collection, grouping start/end year pairs.
    /// </summary>
    async Task<List<EraRange>> LoadErasAsync(
        Continuity? continuity = null,
        CancellationToken ct = default
    )
    {
        var eraCollName = Collections.TimelinePrefix + "Era";
        var collectionNames = await _timelineDb.ListCollectionNamesAsync(cancellationToken: ct);
        if (!collectionNames.ToList().Contains(eraCollName))
            return [];

        var eraCollection = _timelineDb.GetCollection<TimelineEvent>(eraCollName);
        var filter =
            BuildGlobalFilter(continuity, null)
            & Builders<TimelineEvent>.Filter.Ne(e => e.Year, null);
        var eras = await eraCollection.Find(filter).ToListAsync(ct);

        // Group by title → take min/max year as range
        var grouped = eras.GroupBy(e => StripLegendsSuffix(e.Title ?? ""))
            .Where(g => !string.IsNullOrWhiteSpace(g.Key));

        var ranges = new List<EraRange>();
        foreach (var group in grouped)
        {
            var sortKeys = group
                .Select(e =>
                {
                    var year = (int)(e.Year ?? 0);
                    return e.Demarcation == Demarcation.Bby ? -year : year;
                })
                .OrderBy(k => k)
                .ToList();

            var start = sortKeys.First();
            var end = sortKeys.Last();
            if (start == end)
                end = start + 1; // single-point era

            ranges.Add(
                new EraRange
                {
                    Name = group.Key,
                    Start = start,
                    End = end,
                }
            );
        }

        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        return ranges;
    }

    static string StripLegendsSuffix(string title)
    {
        if (title.EndsWith("/Legends", StringComparison.OrdinalIgnoreCase))
            return title[..^"/Legends".Length];
        return title;
    }

    /// <summary>
    /// Build the location name → grid coordinate lookup from Systems and CelestialBodies.
    /// </summary>
    async Task<Dictionary<string, (int col, int row, string? region)>> BuildLocationLookupAsync(
        CancellationToken ct
    )
    {
        var lookup = new Dictionary<string, (int col, int row, string? region)>(
            StringComparer.OrdinalIgnoreCase
        );

        // Systems with grid squares
        var sysFilter = Builders<Page>.Filter.And(
            Builders<Page>.Filter.Regex(
                "infobox.Template",
                new BsonRegularExpression(":System$", "i")
            ),
            Builders<Page>.Filter.ElemMatch<BsonDocument>(
                "infobox.Data",
                new BsonDocument("Label", "Grid square")
            )
        );

        var sysRecs = await _pages
            .Find(sysFilter)
            .Project<Page>(Builders<Page>.Projection.Include(p => p.Title).Include("infobox.Data"))
            .ToListAsync(ct);

        foreach (var rec in sysRecs)
        {
            var grid = GetFirstDataValue(rec, "Grid square");
            if (TryParseGridSquare(grid, out var col, out var row))
            {
                var region = GetFirstDataValue(rec, "Region") is { } rn
                    ? MapService.NormalizeRegionName(rn)
                    : null;
                lookup.TryAdd(rec.Title, (col, row, region));
            }
        }

        // CelestialBodies with grid squares
        var cbFilter = Builders<Page>.Filter.And(
            Builders<Page>.Filter.Regex(
                "infobox.Template",
                new BsonRegularExpression(":CelestialBody$", "i")
            ),
            Builders<Page>.Filter.ElemMatch<BsonDocument>(
                "infobox.Data",
                new BsonDocument("Label", "Grid square")
            )
        );

        var cbRecs = await _pages
            .Find(cbFilter)
            .Project<Page>(Builders<Page>.Projection.Include(p => p.Title).Include("infobox.Data"))
            .ToListAsync(ct);

        foreach (var rec in cbRecs)
        {
            var grid = GetFirstDataValue(rec, "Grid square");
            if (TryParseGridSquare(grid, out var col, out var row))
            {
                var region = GetFirstDataValue(rec, "Region") is { } rn
                    ? MapService.NormalizeRegionName(rn)
                    : null;
                lookup.TryAdd(rec.Title, (col, row, region));
            }
        }

        _logger.LogInformation(
            "GalaxyEvents: built location lookup with {Count} entries",
            lookup.Count
        );
        return lookup;
    }

    /// <summary>
    /// Get the overview: regions, timeline range, available lenses, eras.
    /// </summary>
    public async Task<GalaxyEventsOverview> GetOverviewAsync(
        Continuity? continuity = null,
        Universe? universe = null,
        CancellationToken ct = default
    )
    {
        var overview = new GalaxyEventsOverview();

        // Regions from systems (same logic as MapService V2)
        var sysFilter = Builders<Page>.Filter.And(
            Builders<Page>.Filter.Regex(
                "infobox.Template",
                new BsonRegularExpression(":System$", "i")
            ),
            Builders<Page>.Filter.ElemMatch<BsonDocument>(
                "infobox.Data",
                new BsonDocument("Label", "Grid square")
            )
        );

        var sysRecs = await _pages
            .Find(sysFilter)
            .Project<Page>(Builders<Page>.Projection.Include(p => p.Title).Include("infobox.Data"))
            .ToListAsync(ct);

        var regionCells = new Dictionary<string, HashSet<(int col, int row)>>(
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var rec in sysRecs)
        {
            var grid = GetFirstDataValue(rec, "Grid square");
            if (!TryParseGridSquare(grid, out var col, out var row))
                continue;
            var regionRaw = GetFirstDataValue(rec, "Region");
            if (string.IsNullOrEmpty(regionRaw))
                continue;
            var region = MapService.NormalizeRegionName(regionRaw);
            if (!regionCells.TryGetValue(region, out var cells))
            {
                cells = [];
                regionCells[region] = cells;
            }
            cells.Add((col, row));
        }

        foreach (var (name, cells) in regionCells)
        {
            overview.Regions.Add(
                new MapV2Region
                {
                    Name = name,
                    Cells = cells.Select(c => new[] { c.col, c.row }).ToList(),
                }
            );
        }

        // Discover lenses dynamically
        overview.LensSummaries = await DiscoverLensesAsync(continuity, ct);
        overview.AvailableLenses = overview.LensSummaries.Select(s => s.Lens).ToList();

        // Load eras from Era collection
        overview.Eras = await LoadErasAsync(continuity, ct);

        // Timeline range — aggregate min/max years across all event collections
        var allYears = new List<TimelineYearEntry>();

        // Build continuity match for the aggregation pipeline
        var yearMatchDoc = new BsonDocument("Year", new BsonDocument("$ne", BsonNull.Value));
        if (continuity is not null && continuity != Continuity.Both)
        {
            yearMatchDoc["Continuity"] = new BsonDocument(
                "$in",
                new BsonArray
                {
                    (int)continuity.Value,
                    (int)Continuity.Both,
                    (int)Continuity.Unknown,
                }
            );
        }

        foreach (var lens in overview.AvailableLenses)
        {
            var collection = _timelineDb.GetCollection<BsonDocument>(
                Collections.TimelinePrefix + lens
            );

            var pipeline = new[]
            {
                new BsonDocument("$match", yearMatchDoc),
                new BsonDocument(
                    "$group",
                    new BsonDocument
                    {
                        {
                            "_id",
                            new BsonDocument
                            {
                                { "Year", new BsonDocument("$toInt", "$Year") },
                                { "Demarcation", "$Demarcation" },
                            }
                        },
                        { "count", new BsonDocument("$sum", 1) },
                    }
                ),
            };

            var cursor = await collection.AggregateAsync<BsonDocument>(
                pipeline,
                cancellationToken: ct
            );
            var results = await cursor.ToListAsync(ct);

            foreach (var doc in results)
            {
                var id = doc["_id"].AsBsonDocument;
                var year = id["Year"].AsInt32;
                var demarcationRaw = id["Demarcation"];
                var demarcation =
                    demarcationRaw.BsonType == BsonType.String ? demarcationRaw.AsString
                    : demarcationRaw.AsInt32 == (int)Demarcation.Bby ? "Bby"
                    : "Aby";

                var isBby =
                    demarcation.Equals("Bby", StringComparison.OrdinalIgnoreCase)
                    || demarcation.Equals("BBY", StringComparison.OrdinalIgnoreCase);

                var sortKey = isBby ? -year : year;
                var displayDemarcation = isBby ? "BBY" : "ABY";

                var existing = allYears.Find(y =>
                    y.Year == year && y.Demarcation == displayDemarcation
                );
                if (existing != null)
                {
                    existing.EventCount += doc["count"].AsInt32;
                }
                else
                {
                    allYears.Add(
                        new TimelineYearEntry
                        {
                            Year = year,
                            Demarcation = displayDemarcation,
                            SortKey = sortKey,
                            EventCount = doc["count"].AsInt32,
                        }
                    );
                }
            }
        }

        allYears.Sort((a, b) => a.SortKey.CompareTo(b.SortKey));

        // Use the actual data range rather than hardcoded limits
        overview.TimelineRange = new TimelineRange
        {
            MinYear = allYears.FirstOrDefault()?.SortKey ?? -1000,
            MaxYear = allYears.LastOrDefault()?.SortKey ?? 150,
            Years = allYears,
        };

        _logger.LogInformation(
            "GalaxyEvents overview: {Regions} regions, {Lenses} lenses, {Eras} eras, {Years} distinct years",
            overview.Regions.Count,
            overview.AvailableLenses.Count,
            overview.Eras.Count,
            allYears.Count
        );

        return overview;
    }

    /// <summary>
    /// Get event layer data for a specific lens and time window.
    /// </summary>
    public async Task<GalaxyEventLayer> GetEventLayerAsync(
        string lens,
        int yearStart,
        int yearEnd,
        Continuity? continuity = null,
        Universe? universe = null,
        CancellationToken ct = default
    )
    {
        var layer = new GalaxyEventLayer
        {
            Lens = lens,
            YearStart = yearStart,
            YearEnd = yearEnd,
        };

        var locationLookup = await BuildLocationLookupAsync(ct);
        var collectionNames = (
            await _timelineDb.ListCollectionNamesAsync(cancellationToken: ct)
        ).ToList();

        // Determine which collections to query
        List<string> lensCollections;
        if (lens == "All")
        {
            var summaries = await DiscoverLensesAsync(continuity, ct);
            lensCollections = summaries.Select(s => s.Lens).ToList();
        }
        else
        {
            var fullName = Collections.TimelinePrefix + lens;
            lensCollections = collectionNames.Contains(fullName) ? [lens] : [];
        }

        var allEvents = new List<(TimelineEvent evt, string collection)>();

        foreach (var collName in lensCollections)
        {
            var collection = _timelineDb.GetCollection<TimelineEvent>(
                Collections.TimelinePrefix + collName
            );

            var filters = new List<FilterDefinition<TimelineEvent>>
            {
                Builders<TimelineEvent>.Filter.Ne(e => e.Year, null),
                BuildGlobalFilter(continuity, universe),
            };

            if (yearStart < 0 && yearEnd < 0)
            {
                filters.Add(
                    Builders<TimelineEvent>.Filter.And(
                        Builders<TimelineEvent>.Filter.Eq(e => e.Demarcation, Demarcation.Bby),
                        Builders<TimelineEvent>.Filter.Lte(e => e.Year, (float)Math.Abs(yearStart)),
                        Builders<TimelineEvent>.Filter.Gte(e => e.Year, (float)Math.Abs(yearEnd))
                    )
                );
            }
            else if (yearStart >= 0 && yearEnd >= 0)
            {
                filters.Add(
                    Builders<TimelineEvent>.Filter.And(
                        Builders<TimelineEvent>.Filter.Eq(e => e.Demarcation, Demarcation.Aby),
                        Builders<TimelineEvent>.Filter.Gte(e => e.Year, (float)yearStart),
                        Builders<TimelineEvent>.Filter.Lte(e => e.Year, (float)yearEnd)
                    )
                );
            }
            else
            {
                var bbyPart = Builders<TimelineEvent>.Filter.And(
                    Builders<TimelineEvent>.Filter.Eq(e => e.Demarcation, Demarcation.Bby),
                    Builders<TimelineEvent>.Filter.Lte(e => e.Year, (float)Math.Abs(yearStart))
                );
                var abyPart = Builders<TimelineEvent>.Filter.And(
                    Builders<TimelineEvent>.Filter.Eq(e => e.Demarcation, Demarcation.Aby),
                    Builders<TimelineEvent>.Filter.Lte(e => e.Year, (float)yearEnd)
                );
                filters.Add(Builders<TimelineEvent>.Filter.Or(bbyPart, abyPart));
            }

            var events = await collection
                .Find(Builders<TimelineEvent>.Filter.And(filters))
                .ToListAsync(ct);
            foreach (var evt in events)
                allEvents.Add((evt, collName));
        }

        layer.TotalEvents = allEvents.Count;

        var cellMap = new Dictionary<(int col, int row), GalaxyEventCell>();
        foreach (var (evt, collName) in allEvents)
        {
            var placeNames = GetPlaceNames(evt);
            var mapped = false;

            foreach (var placeName in placeNames)
            {
                if (locationLookup.TryGetValue(placeName, out var loc))
                {
                    var key = (loc.col, loc.row);
                    if (!cellMap.TryGetValue(key, out var cell))
                    {
                        cell = new GalaxyEventCell
                        {
                            Col = loc.col,
                            Row = loc.row,
                            Region = loc.region,
                        };
                        cellMap[key] = cell;
                    }

                    cell.Count++;
                    if (cell.Events.Count < 10)
                    {
                        cell.Events.Add(
                            new GalaxyEventMarker
                            {
                                Title = evt.Title ?? "Unknown",
                                Collection = collName,
                                Year = (int)(evt.Year ?? 0),
                                Demarcation = evt.Demarcation == Demarcation.Bby ? "BBY" : "ABY",
                                Place = placeName,
                                Outcome = evt
                                    .Properties.FirstOrDefault(p => p.Label == "Outcome")
                                    ?.Values.FirstOrDefault(),
                                ImageUrl = evt.ImageUrl,
                            }
                        );
                    }
                    mapped = true;
                    break;
                }
            }
            if (mapped)
                layer.MappedEvents++;
        }

        var maxCount = cellMap.Values.Count > 0 ? cellMap.Values.Max(c => c.Count) : 1;
        foreach (var cell in cellMap.Values)
            cell.Intensity = maxCount > 0 ? (double)cell.Count / maxCount : 0;

        layer.Cells = cellMap.Values.OrderByDescending(c => c.Count).ToList();

        _logger.LogInformation(
            "GalaxyEvents layer [{Lens}] years [{Start},{End}]: {Total} events, {Mapped} mapped to {Cells} cells",
            lens,
            yearStart,
            yearEnd,
            layer.TotalEvents,
            layer.MappedEvents,
            layer.Cells.Count
        );

        return layer;
    }

    /// <summary>
    /// Get per-year event density for a specific lens.
    /// </summary>
    public async Task<LensDensity> GetLensDensityAsync(
        string lens,
        Continuity? continuity = null,
        Universe? universe = null,
        CancellationToken ct = default
    )
    {
        var density = new LensDensity { Lens = lens };
        var collectionNames = (
            await _timelineDb.ListCollectionNamesAsync(cancellationToken: ct)
        ).ToList();

        List<string> lensCollections;
        if (lens == "All")
        {
            var summaries = await DiscoverLensesAsync(continuity, ct);
            lensCollections = summaries.Select(s => s.Lens).ToList();
        }
        else
        {
            var fullName = Collections.TimelinePrefix + lens;
            lensCollections = collectionNames.Contains(fullName) ? [lens] : [];
        }

        var yearCounts = new Dictionary<int, int>();
        foreach (var collName in lensCollections)
        {
            var collection = _timelineDb.GetCollection<BsonDocument>(
                Collections.TimelinePrefix + collName
            );

            var pipeline = new[]
            {
                new BsonDocument(
                    "$match",
                    new BsonDocument("Year", new BsonDocument("$ne", BsonNull.Value))
                ),
                new BsonDocument(
                    "$group",
                    new BsonDocument
                    {
                        {
                            "_id",
                            new BsonDocument
                            {
                                { "Year", new BsonDocument("$toInt", "$Year") },
                                { "Demarcation", "$Demarcation" },
                            }
                        },
                        { "count", new BsonDocument("$sum", 1) },
                    }
                ),
            };

            var cursor = await collection.AggregateAsync<BsonDocument>(
                pipeline,
                cancellationToken: ct
            );
            var results = await cursor.ToListAsync(ct);

            foreach (var doc in results)
            {
                var id = doc["_id"].AsBsonDocument;
                var year = id["Year"].AsInt32;
                var demarcationRaw = id["Demarcation"];
                var demStr =
                    demarcationRaw.BsonType == BsonType.String ? demarcationRaw.AsString
                    : demarcationRaw.AsInt32 == (int)Demarcation.Bby ? "Bby"
                    : "Aby";

                var isBby =
                    demStr.Equals("Bby", StringComparison.OrdinalIgnoreCase)
                    || demStr.Equals("BBY", StringComparison.OrdinalIgnoreCase);
                var sortKey = isBby ? -year : year;

                yearCounts[sortKey] = yearCounts.GetValueOrDefault(sortKey) + doc["count"].AsInt32;
            }
        }

        var maxCount = yearCounts.Count > 0 ? yearCounts.Values.Max() : 1;
        density.Buckets = yearCounts
            .OrderBy(kv => kv.Key)
            .Select(kv => new DensityBucket
            {
                SortKey = kv.Key,
                Count = kv.Value,
                Intensity = maxCount > 0 ? (double)kv.Value / maxCount : 0,
            })
            .ToList();

        return density;
    }

    /// <summary>
    /// Get a paginated, searchable list of events for the event browser.
    /// </summary>
    public async Task<GalaxyEventList> GetEventListAsync(
        string lens,
        string? search,
        int page,
        int pageSize,
        Continuity? continuity = null,
        Universe? universe = null,
        CancellationToken ct = default
    )
    {
        var result = new GalaxyEventList
        {
            Lens = lens,
            Page = page,
            PageSize = pageSize,
        };
        var collectionNames = (
            await _timelineDb.ListCollectionNamesAsync(cancellationToken: ct)
        ).ToList();

        List<string> lensCollections;
        if (lens == "All")
        {
            var summaries = await DiscoverLensesAsync(continuity, ct);
            lensCollections = summaries.Select(s => s.Lens).ToList();
        }
        else
        {
            var fullName = Collections.TimelinePrefix + lens;
            lensCollections = collectionNames.Contains(fullName) ? [lens] : [];
        }

        var locationLookup = await BuildLocationLookupAsync(ct);
        var allItems = new List<GalaxyEventListItem>();

        foreach (var collName in lensCollections)
        {
            var collection = _timelineDb.GetCollection<TimelineEvent>(
                Collections.TimelinePrefix + collName
            );

            var filter = Builders<TimelineEvent>.Filter.And(
                Builders<TimelineEvent>.Filter.Ne(e => e.Year, null),
                BuildGlobalFilter(continuity, universe)
            );
            if (!string.IsNullOrWhiteSpace(search))
            {
                filter = Builders<TimelineEvent>.Filter.And(
                    filter,
                    Builders<TimelineEvent>.Filter.Regex(
                        e => e.Title,
                        new BsonRegularExpression(search, "i")
                    )
                );
            }

            var events = await collection
                .Find(filter)
                .Project(
                    Builders<TimelineEvent>
                        .Projection.Include(e => e.Title)
                        .Include(e => e.Year)
                        .Include(e => e.Demarcation)
                        .Include(e => e.Properties)
                )
                .As<TimelineEvent>()
                .ToListAsync(ct);

            foreach (var evt in events)
            {
                var isBby = evt.Demarcation == Demarcation.Bby;
                var yearInt = (int)(evt.Year ?? 0);
                var sortKey = isBby ? -yearInt : yearInt;

                var placeNames = GetPlaceNames(evt);
                var firstPlace = placeNames.FirstOrDefault();
                var hasLocation = placeNames.Any(p => locationLookup.ContainsKey(p));

                allItems.Add(
                    new GalaxyEventListItem
                    {
                        Title = evt.Title ?? "Unknown",
                        Year = yearInt,
                        Demarcation = isBby ? "BBY" : "ABY",
                        SortKey = sortKey,
                        Place = firstPlace,
                        Collection = collName,
                        HasLocation = hasLocation,
                    }
                );
            }
        }

        allItems.Sort((a, b) => a.SortKey.CompareTo(b.SortKey));
        result.Total = allItems.Count;
        result.Items = allItems.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return result;
    }

    /// <summary>
    /// Extract place names from a timeline event's Properties using all location-relevant labels.
    /// </summary>
    static List<string> GetPlaceNames(TimelineEvent evt)
    {
        var places = new List<string>();
        foreach (var prop in evt.Properties)
        {
            if (prop.Label == null)
                continue;

            // Check against all location-relevant labels
            var isLocationLabel = false;
            foreach (var label in LocationLabels)
            {
                if (prop.Label.Equals(label, StringComparison.OrdinalIgnoreCase))
                {
                    isLocationLabel = true;
                    break;
                }
            }
            if (!isLocationLabel)
                continue;

            // Prefer link content (canonical names) over raw values
            foreach (var link in prop.Links)
            {
                if (!string.IsNullOrWhiteSpace(link.Content))
                    places.Add(link.Content.Trim());
            }

            if (places.Count == 0)
            {
                foreach (var val in prop.Values)
                {
                    if (!string.IsNullOrWhiteSpace(val))
                        places.Add(val.Trim());
                }
            }
        }

        return places;
    }

    static string? GetFirstDataValue(Page page, string label) =>
        page.Infobox?.Data.FirstOrDefault(d => d.Label == label)?.Values.FirstOrDefault();

    static bool TryParseGridSquare(string? gridSquare, out int col, out int row)
    {
        col = 0;
        row = 0;
        if (string.IsNullOrWhiteSpace(gridSquare))
            return false;
        var parts = gridSquare.Split('-', 2);
        if (parts.Length != 2)
            return false;
        var letter = parts[0].Trim().ToUpperInvariant();
        if (letter.Length != 1 || letter[0] < 'A' || letter[0] > 'Z')
            return false;
        if (!int.TryParse(parts[1].Trim(), out var num) || num < 1 || num > 20)
            return false;
        col = letter[0] - 'A';
        row = num - 1;
        return true;
    }
}
