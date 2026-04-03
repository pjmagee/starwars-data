using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services;

/// <summary>
/// Builds the knowledge graph deterministically from infobox data.
/// No LLM calls — classifies each infobox field as a property (scalar)
/// or relationship (link to another entity) based on field name and link presence.
/// </summary>
public class InfoboxGraphService(
    IMongoClient mongoClient,
    IOptions<SettingsOptions> settings,
    ILogger<InfoboxGraphService> logger
)
{
    readonly IMongoCollection<Page> _pages = mongoClient
        .GetDatabase(settings.Value.DatabaseName)
        .GetCollection<Page>(Collections.Pages);
    readonly IMongoCollection<GraphNode> _nodes = mongoClient
        .GetDatabase(settings.Value.DatabaseName)
        .GetCollection<GraphNode>(Collections.KgNodes);
    readonly IMongoCollection<RelationshipEdge> _edges = mongoClient
        .GetDatabase(settings.Value.DatabaseName)
        .GetCollection<RelationshipEdge>(Collections.KgEdges);

    // ── Field classification ──
    // Fields are classified per infobox type. A field is a PROPERTY if it holds scalar
    // data about the entity (height, color, name). A field is a RELATIONSHIP if it links
    // to other entities in the wiki (homeworld, species, affiliation).
    //
    // The heuristic: if a field almost always contains wiki links AND the link target
    // represents a meaningful entity (not just a color page or unit page), it's a relationship.

    /// <summary>
    /// Fields that are ALWAYS properties regardless of whether they contain links.
    /// These represent scalar attributes of an entity, not connections to other entities.
    /// </summary>
    static readonly HashSet<string> AlwaysProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        // Identity / titles
        "Titles",
        "Pronouns",
        "Gender",
        // Physical attributes (links go to color/unit pages, not entities)
        "Height",
        "Mass",
        "Eye color",
        "Hair color",
        "Skin color",
        "Feather color",
        "Plating color",
        "Sensor color",
        "Color",
        "Average height",
        "Average length",
        "Average mass",
        "Average lifespan",
        "Average wingspan",
        // Descriptions / classifications
        "Class",
        "Designation",
        "Classification",
        "Distinctions",
        "Demonym",
        "Organization type",
        "Model",
        "Shape",
        "Purpose",
        // Measurements / specs
        "Length",
        "Width",
        "Height/depth",
        "Diameter",
        "Weight",
        "Hyperdrive rating",
        "Maximum atmospheric speed",
        "Megalight per hour",
        "Maximum altitude",
        "Maximum speed",
        "Maximum depth",
        "Passengers",
        "Population",
        "Consumables",
        "Cargo capacity",
        "Orbital position",
        "Orbital period",
        "Rotation period",
        "Surface water",
        // Publication / media metadata
        "Pages",
        "Media type",
        "Issue number",
        "Issue #",
        "Issues",
        "Format",
        "Run time",
        "Number of players",
        "Ages of players",
        "Playing time",
        "Number of books",
        "Number of audiobooks",
        "Collected issues",
        "Skills required",
        // Outcomes / descriptions
        "Outcome",
        "Result",
        "Diet",
        "Habitat",
        "Atmosphere",
        "Terrain",
        "Climate",
        "Grid square",
        "Grid coordinates",
        // Misc scalar
        "Symptoms",
        "Transmission type",
        "Incubation period",
        "Number infected",
        "Number killed",
        "Other markings",
        "Other systems",
        "Major exports",
        "Major imports",
    };

    /// <summary>
    /// Fields that are ALWAYS relationships — their linked targets are meaningful entities.
    /// Maps field name → edge label for the relationship.
    /// </summary>
    static readonly Dictionary<string, string> RelationshipLabels = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        // ── Character ──
        ["Species"] = "species",
        ["Homeworld"] = "homeworld",
        ["Affiliation(s)"] = "affiliated_with",
        ["Affiliation"] = "affiliated_with",
        ["Family"] = "family",
        ["Children"] = "parent_of",
        ["Parent(s)"] = "child_of",
        ["Sibling(s)"] = "sibling_of",
        ["Partner(s)"] = "partner_of",
        ["Masters"] = "apprentice_of",
        ["Apprentices"] = "master_of",
        ["Genetic donor(s)"] = "cloned_from",
        ["Owner(s)"] = "owned_by",
        ["Owners"] = "owned_by",
        ["Cybernetics"] = "has_cybernetics",
        ["Songs"] = "performs_song",
        ["Collaborations"] = "collaborates_with",

        // ── Location (CelestialBody, System, Sector, Region, City) ──
        ["Region"] = "in_region",
        ["Region(s)"] = "in_region",
        ["System"] = "in_system",
        ["Sector"] = "in_sector",
        ["Orbited body"] = "orbits",
        ["Orbiting bodies"] = "orbited_by",
        ["Points of interest"] = "has_point_of_interest",
        ["Major cities"] = "has_city",
        ["Native species"] = "has_native_species",
        ["Other species"] = "has_species",
        ["Flora"] = "has_flora",
        ["Fauna"] = "has_fauna",
        ["Trade routes"] = "on_trade_route",
        ["Government"] = "governed_by",
        ["Primary language(s)"] = "speaks_language",
        ["Celestial body"] = "on_celestial_body",
        ["Location"] = "located_at",
        ["Location(s)"] = "located_at",
        ["Locations"] = "located_at",
        ["Continent"] = "on_continent",
        ["Suns"] = "orbits_star",
        ["Moons"] = "has_moon",
        ["Space stations"] = "has_space_station",
        ["Asteroids"] = "has_asteroid",
        ["Nebulae"] = "has_nebula",
        ["Comets"] = "has_comet",
        ["Other objects"] = "has_object",
        ["Builder"] = "built_by",

        // ── Organization / Government ──
        ["Capital"] = "has_capital",
        ["Headquarters"] = "headquartered_at",
        ["Leader(s)"] = "led_by",
        ["Founder(s)"] = "founded_by",
        ["Head of state"] = "head_of_state",
        ["Head of government"] = "head_of_government",
        ["Commander-in-chief"] = "commander_in_chief",
        ["Military branch"] = "has_military_branch",
        ["Sub-group(s)"] = "has_subgroup",
        ["Formed from"] = "formed_from",
        ["Executive branch"] = "has_executive_branch",
        ["Legislative branch"] = "has_legislative_branch",
        ["Judicial branch"] = "has_judicial_branch",
        ["State religious body"] = "has_state_religion",
        ["Currency"] = "uses_currency",
        ["Official language"] = "official_language",
        ["Constitution"] = "has_constitution",
        ["Founding document"] = "has_founding_document",
        ["Official holiday"] = "has_holiday",
        ["Anthem"] = "has_anthem",
        ["Associations"] = "associated_with",
        ["Parent company"] = "subsidiary_of",
        ["Subsidiaries"] = "has_subsidiary",
        ["Major product(s)"] = "produces",

        // ── Ship / Vehicle ──
        ["Crew"] = "has_crew_role",
        ["Cost"] = "priced_in",
        ["Manufacturer"] = "manufactured_by",
        ["Type"] = "type_of",
        ["Line"] = "product_line",
        ["Armament"] = "armed_with",
        ["Engine unit(s)"] = "has_engine",
        ["Hyperdrive system"] = "has_hyperdrive",
        ["Shielding"] = "has_shielding",
        ["Sensor systems"] = "has_sensors",
        ["Navigation system"] = "has_navigation",
        ["Complement"] = "carries_complement",
        ["Equipment"] = "has_equipment",
        ["Degree"] = "droid_degree",
        ["Product line"] = "product_line",
        ["Material(s)"] = "made_of",

        // ── Battle / Conflict ──
        ["Place"] = "took_place_at",
        ["Conflict"] = "part_of_conflict",
        ["Major battles"] = "includes_battle",
        ["Battles"] = "includes_battle",
        ["Date"] = "occurred_at",
        ["Begin"] = "began_at",

        // ── Species ──
        ["Point of origin"] = "originates_from",
        ["Origin"] = "originates_from",
        ["Language"] = "speaks_language",
        ["Language(s)"] = "speaks_language",
        ["Subspecies"] = "has_subspecies",
        ["Races"] = "has_race",

        // ── Publication / Media ──
        ["Publisher"] = "published_by",
        ["Author(s)"] = "authored_by",
        ["Writer"] = "written_by",
        ["Writer(s)"] = "written_by",
        ["Penciller"] = "illustrated_by",
        ["Penciller(s)"] = "illustrated_by",
        ["Colorist"] = "colored_by",
        ["Colorist(s)"] = "colored_by",
        ["Letterer"] = "lettered_by",
        ["Letterer(s)"] = "lettered_by",
        ["Cover artist"] = "cover_by",
        ["Cover artist(s)"] = "cover_by",
        ["Illustrator"] = "illustrated_by",
        ["Illustrator(s)"] = "illustrated_by",
        ["Narrator(s)"] = "narrated_by",
        ["Director(s)"] = "directed_by",
        ["Series"] = "part_of_series",
        ["Published in"] = "published_in",
        ["Part of"] = "part_of",
        ["Timeline"] = "in_timeline",
        ["Release date"] = "released_on",
        ["Publication date"] = "published_on",
        ["Start date"] = "started_on",
        ["End date"] = "ended_on",
        ["First published"] = "first_published_on",
        ["Last published"] = "last_published_on",
        ["First book published"] = "first_published_on",
        ["Last book published"] = "last_published_on",
        ["Followed by"] = "followed_by",
        ["Preceded by"] = "preceded_by",
        ["Next"] = "followed_by",
        ["Previous"] = "preceded_by",
        ["Subsequent"] = "followed_by",
        ["Game"] = "for_game",
        ["Campaign"] = "part_of_campaign",

        // ── Era / Year ──
        ["Years"] = "spans_years",
        ["Important events"] = "has_important_event",
        ["Conflicts"] = "has_conflict",

        // ── Religion / Deity ──
        ["Associated religion"] = "associated_religion",
        ["Area of influence"] = "influences",
        ["Places of worship"] = "worshipped_at",
        ["Pantheon"] = "in_pantheon",
        ["Artifacts"] = "has_artifact",

        // ── Disease ──
        ["Susceptible species"] = "affects_species",
        ["Treatments"] = "treated_by",
        ["Created by"] = "created_by",
        ["Creators"] = "created_by",

        // ── Cultural / Social ──
        ["Culture"] = "associated_culture",
        ["Socio-cultural group(s)"] = "associated_culture",
        ["Type of group"] = "group_type",

        // ── Election ──
        ["Candidates"] = "has_candidate",
        ["Electorate"] = "has_electorate",

        // ── Family ──
        ["Members"] = "has_member",
        ["Homeworld(s)"] = "homeworld",

        // ── Fleet / Military ──
        ["Flagship(s)"] = "has_flagship",
        ["Commander(s)"] = "commanded_by",
        ["Notable members"] = "has_notable_member",
        ["Notable units"] = "has_notable_unit",

        // ── Sports / Competition ──
        ["Competitors"] = "has_competitor",
        ["League"] = "in_league",
        ["Organizer(s)"] = "organized_by",
        ["Host(s)"] = "hosted_by",

        // ── Misc ──
        ["Mayor"] = "led_by",
    };

    /// <summary>
    /// Unified map of all temporal infobox fields → (semantic, calendarHint).
    /// Calendar hint: "galactic" (BBY/ABY), "real" (CE), "auto" (detect from text).
    /// See eng/design/001-temporal-facets.md for the full design rationale.
    /// </summary>
    static readonly Dictionary<string, (string semantic, string calendar)> TemporalFieldMap = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        // ── Lifespan (auto: Character=galactic, Person=real) ──
        ["Born"] = ("lifespan.start", "auto"),
        ["Died"] = ("lifespan.end", "auto"),

        // ── Conflict / Event ──
        ["Beginning"] = ("conflict.start", "galactic"),
        ["Begin"] = ("conflict.start", "galactic"),
        ["Begins"] = ("conflict.start", "galactic"),
        ["End"] = ("conflict.end", "galactic"),
        ["Ends"] = ("conflict.end", "galactic"),
        ["Date"] = ("conflict.point", "galactic"),

        // ── Construction ──
        ["Constructed"] = ("construction.start", "galactic"),
        ["Commissioned"] = ("construction.start", "galactic"),
        ["Rebuilt"] = ("construction.rebuilt", "galactic"),
        ["Destroyed"] = ("construction.end", "galactic"),
        ["Retired"] = ("construction.end", "galactic"),

        // ── Creation (artifacts, devices, weapons) ──
        ["Date created"] = ("creation.start", "galactic"),
        ["Date engineered"] = ("creation.start", "galactic"),
        ["Date introduced"] = ("creation.start", "galactic"),
        ["Year introduced"] = ("creation.start", "galactic"),
        ["Date destroyed"] = ("creation.end", "galactic"),
        ["Date retired"] = ("creation.end", "galactic"),
        ["Date discovered"] = ("creation.discovered", "galactic"),

        // ── Institutional lifecycle ──
        ["Date established"] = ("institutional.start", "galactic"),
        ["Date founded"] = ("institutional.start", "galactic"),
        ["Founding"] = ("institutional.start", "galactic"),
        ["Founded"] = ("institutional.start", "auto"),
        ["Established"] = ("institutional.start", "galactic"),
        ["Date dissolved"] = ("institutional.end", "galactic"),
        ["Dissolved"] = ("institutional.end", "auto"),
        ["Date abolished"] = ("institutional.end", "galactic"),
        ["Date of collapse"] = ("institutional.end", "galactic"),
        ["Closed"] = ("institutional.end", "auto"),
        ["Date closed"] = ("institutional.end", "auto"),
        ["Date reorganized"] = ("institutional.reorganized", "galactic"),
        ["Reorganized"] = ("institutional.reorganized", "galactic"),
        ["Reorganization"] = ("institutional.reorganized", "galactic"),
        ["Date restored"] = ("institutional.restored", "galactic"),
        ["Restored"] = ("institutional.restored", "galactic"),
        ["Date reestablished"] = ("institutional.restored", "galactic"),
        ["Date of restoration"] = ("institutional.restored", "galactic"),
        ["Date fragmented"] = ("institutional.fragmented", "galactic"),
        ["Fragmented"] = ("institutional.fragmented", "galactic"),
        ["Fragmentation"] = ("institutional.fragmented", "galactic"),
        ["Date suspended"] = ("institutional.suspended", "galactic"),

        // ── Publication (always real-world) ──
        ["Release date"] = ("publication.release", "real"),
        ["Publication date"] = ("publication.release", "real"),
        ["Air date"] = ("publication.release", "real"),
        ["First released"] = ("publication.release", "real"),
        ["Released"] = ("publication.release", "real"),
        ["Premiere date"] = ("publication.release", "real"),
        ["Launched"] = ("publication.release", "real"),
        ["Start date"] = ("publication.start", "real"),
        ["First aired"] = ("publication.start", "real"),
        ["First published"] = ("publication.start", "real"),
        ["First book published"] = ("publication.start", "real"),
        ["First media published"] = ("publication.start", "real"),
        ["First issue"] = ("publication.start", "real"),
        ["End date"] = ("publication.end", "real"),
        ["Last aired"] = ("publication.end", "real"),
        ["Last published"] = ("publication.end", "real"),
        ["Last book published"] = ("publication.end", "real"),
        ["Last issue"] = ("publication.end", "real"),
        ["Closing date"] = ("publication.end", "real"),

        // ── Niche temporal fields ──
        ["First awarded"] = ("usage.start", "galactic"),
        ["Last awarded"] = ("usage.end", "galactic"),
        ["First employed"] = ("usage.start", "galactic"),
        ["Last employed"] = ("usage.end", "galactic"),
        ["First played"] = ("usage.start", "galactic"),
        ["First worshipped"] = ("usage.start", "galactic"),
    };

    /// <summary>
    /// Build the knowledge graph from all pages with infoboxes.
    /// Creates GraphNode documents (with properties) and RelationshipEdge documents (for links).
    /// </summary>
    public async Task BuildGraphAsync(CancellationToken ct = default)
    {
        logger.LogInformation("InfoboxGraph: starting graph build from infobox data...");

        // Build a PageId lookup from wiki URLs so we can resolve link targets
        var wikiUrlToPageId = await BuildWikiUrlLookupAsync(ct);
        logger.LogInformation(
            "InfoboxGraph: {Count} wiki URL → PageId mappings",
            wikiUrlToPageId.Count
        );

        // Process all pages with infoboxes
        var filter = Builders<Page>.Filter.Ne(p => p.Infobox, null);
        var totalPages = await _pages.CountDocumentsAsync(filter, cancellationToken: ct);
        logger.LogInformation("InfoboxGraph: {Total} pages with infoboxes to process", totalPages);

        var cursor = await _pages
            .Find(filter)
            .Project(
                Builders<Page>
                    .Projection.Include(p => p.PageId)
                    .Include(p => p.Title)
                    .Include(p => p.Infobox)
                    .Include(p => p.WikiUrl)
                    .Include(p => p.Continuity)
                    .Include(p => p.Universe)
                    .Include(p => p.ContentHash)
            )
            .ToCursorAsync(ct);

        var nodes = new List<GraphNode>();
        var edges = new List<RelationshipEdge>();
        var processed = 0;

        while (await cursor.MoveNextAsync(ct))
        {
            foreach (var doc in cursor.Current)
            {
                var pageId = doc["_id"].AsInt32;
                var title = doc["title"].AsString;
                var continuity = doc.Contains("continuity")
                    ? Enum.TryParse<Continuity>(doc["continuity"].AsString, out var c)
                        ? c
                        : Continuity.Unknown
                    : Continuity.Unknown;
                var universe = doc.Contains("universe")
                    ? Enum.TryParse<Universe>(doc["universe"].AsString, out var u)
                        ? u
                        : Universe.Unknown
                    : Universe.Unknown;
                var contentHash =
                    doc.Contains("contentHash") && !doc["contentHash"].IsBsonNull
                        ? doc["contentHash"].AsString
                        : null;
                var wikiUrl = doc.Contains("wikiUrl") ? doc["wikiUrl"].AsString : null;

                var infoboxDoc = doc["infobox"].AsBsonDocument;
                var template =
                    infoboxDoc.Contains("Template") && !infoboxDoc["Template"].IsBsonNull
                        ? infoboxDoc["Template"].AsString
                        : null;
                var imageUrl =
                    infoboxDoc.Contains("ImageUrl") && !infoboxDoc["ImageUrl"].IsBsonNull
                        ? infoboxDoc["ImageUrl"].AsString
                        : null;

                // Extract type from template URL
                var type = "Unknown";
                if (template is not null)
                {
                    var idx = template.LastIndexOf(':');
                    if (idx >= 0)
                        type = template[(idx + 1)..];
                }

                // Parse infobox data items
                var dataItems =
                    infoboxDoc.Contains("Data") && infoboxDoc["Data"].IsBsonArray
                        ? infoboxDoc["Data"].AsBsonArray
                        : new BsonArray();

                var properties = new Dictionary<string, List<string>>();
                var facets = new List<TemporalFacet>();

                foreach (var item in dataItems)
                {
                    if (item is not BsonDocument itemDoc)
                        continue;
                    var label = itemDoc.Contains("Label") ? itemDoc["Label"].AsString : null;
                    if (label is null)
                        continue;

                    var values =
                        itemDoc.Contains("Values") && itemDoc["Values"].IsBsonArray
                            ? itemDoc["Values"].AsBsonArray.Select(v => v.AsString).ToList()
                            : [];
                    var links =
                        itemDoc.Contains("Links") && itemDoc["Links"].IsBsonArray
                            ? itemDoc["Links"]
                                .AsBsonArray.Where(l => l is BsonDocument)
                                .Select(l => l.AsBsonDocument)
                                .ToList()
                            : [];

                    // Extract temporal facets from known temporal fields
                    if (TemporalFieldMap.TryGetValue(label, out var mapping))
                    {
                        foreach (var val in values)
                        {
                            if (string.IsNullOrWhiteSpace(val))
                                continue;
                            var (calendar, year) = DetectCalendarAndParse(val, mapping.calendar);
                            facets.Add(
                                new TemporalFacet
                                {
                                    Field = label,
                                    Semantic = mapping.semantic,
                                    Calendar = calendar,
                                    Year = year,
                                    Text = val,
                                }
                            );
                        }
                        // Also store the raw text as a property for display
                        if (values.Count > 0)
                            properties[label] = values;
                    }

                    // Classify: property or relationship?
                    if (AlwaysProperties.Contains(label))
                    {
                        // Store as scalar property
                        if (values.Count > 0)
                            properties[label] = values;
                    }
                    else if (
                        RelationshipLabels.TryGetValue(label, out var edgeLabel)
                        || links.Count > 0
                    )
                    {
                        edgeLabel ??= NormaliseLabel(label);
                        var weight = RelationshipLabels.ContainsKey(label) ? 1.0 : 0.8;

                        // Build link lookup: content text → (href, content)
                        var linkLookup = new Dictionary<string, BsonDocument>(
                            StringComparer.OrdinalIgnoreCase
                        );
                        foreach (var link in links)
                        {
                            var content = link.Contains("Content")
                                ? link["Content"].AsString
                                : null;
                            if (content is not null)
                                linkLookup.TryAdd(content, link);
                        }

                        // Match each Value to its primary link (text before parenthetical)
                        var primaryLinks = ExtractPrimaryLinks(values, linkLookup);

                        if (primaryLinks.Count > 0)
                        {
                            // Create edges only for primary entities from Values
                            foreach (var (linkDoc, qualifier, fromYear, toYear) in primaryLinks)
                            {
                                var href = linkDoc.Contains("Href")
                                    ? linkDoc["Href"].AsString
                                    : null;
                                var content = linkDoc.Contains("Content")
                                    ? linkDoc["Content"].AsString
                                    : null;
                                if (href is null || content is null)
                                    continue;

                                var targetPageId = ResolveLinkTarget(
                                    href,
                                    content,
                                    wikiUrlToPageId
                                );

                                edges.Add(
                                    new RelationshipEdge
                                    {
                                        FromId = pageId,
                                        FromName = title,
                                        FromType = type,
                                        ToId = targetPageId,
                                        ToName = content,
                                        ToType = "",
                                        Label = edgeLabel,
                                        Weight = weight,
                                        Evidence = qualifier is not null
                                            ? $"Infobox field '{label}': {content} ({qualifier})"
                                            : $"Infobox field '{label}'",
                                        SourcePageId = pageId,
                                        Continuity = continuity,
                                        FromYear = fromYear,
                                        ToYear = toYear,
                                    }
                                );
                            }
                        }
                        else
                        {
                            // Fallback: no Values or no matches — create edges for all links
                            foreach (var link in links)
                            {
                                var href = link.Contains("Href") ? link["Href"].AsString : null;
                                var content = link.Contains("Content")
                                    ? link["Content"].AsString
                                    : null;
                                if (href is null || content is null)
                                    continue;

                                var targetPageId = ResolveLinkTarget(
                                    href,
                                    content,
                                    wikiUrlToPageId
                                );
                                edges.Add(
                                    new RelationshipEdge
                                    {
                                        FromId = pageId,
                                        FromName = title,
                                        FromType = type,
                                        ToId = targetPageId,
                                        ToName = content,
                                        ToType = "",
                                        Label = edgeLabel,
                                        Weight = weight * 0.8,
                                        Evidence = $"Infobox field '{label}' (fallback)",
                                        SourcePageId = pageId,
                                        Continuity = continuity,
                                    }
                                );
                            }
                        }

                        // If no links at all, store as property
                        if (links.Count == 0 && values.Count > 0)
                            properties[label] = values;
                    }
                }

                // Assign lifecycle ordering within each semantic prefix
                AssignFacetOrder(facets);

                // Compute envelope from facets for fast range queries
                var startFacets = facets
                    .Where(f =>
                        f.Year.HasValue
                        && (
                            f.Semantic.EndsWith(".start")
                            || f.Semantic.EndsWith(".point")
                            || f.Semantic.EndsWith(".release")
                        )
                    )
                    .ToList();
                var endFacets = facets
                    .Where(f =>
                        f.Year.HasValue
                        && (f.Semantic.EndsWith(".end") || f.Semantic.EndsWith(".point"))
                    )
                    .ToList();

                var startYear =
                    startFacets.Count > 0 ? startFacets.Min(f => f.Year!.Value) : (int?)null;
                var endYear = endFacets.Count > 0 ? endFacets.Max(f => f.Year!.Value) : (int?)null;

                // For backward compat: keep startDateText/endDateText from first start/end facet
                var startDateText = startFacets.FirstOrDefault()?.Text;
                var endDateText = endFacets.FirstOrDefault()?.Text;

                nodes.Add(
                    new GraphNode
                    {
                        PageId = pageId,
                        Name = title,
                        Type = type,
                        Continuity = continuity,
                        Universe = universe,
                        Properties = properties,
                        ImageUrl = imageUrl,
                        WikiUrl = wikiUrl,
                        StartYear = startYear,
                        EndYear = endYear,
                        StartDateText = startDateText,
                        EndDateText = endDateText,
                        TemporalFacets = facets,
                        ContentHash = contentHash,
                        ProcessedAt = DateTime.UtcNow,
                    }
                );

                processed++;
                if (processed % 10000 == 0)
                    logger.LogInformation(
                        "InfoboxGraph: processed {Count}/{Total} pages",
                        processed,
                        totalPages
                    );
            }
        }

        logger.LogInformation(
            "InfoboxGraph: processed {Count} pages → {Nodes} nodes, {RawEdges} raw edges",
            processed,
            nodes.Count,
            edges.Count
        );

        // ── Post-processing: filter noise edges and enrich with target type ──
        var nodeTypeMap = nodes.ToDictionary(n => n.PageId, n => n.Type);

        var filteredEdges = new List<RelationshipEdge>(edges.Count);
        int droppedUnresolved = 0,
            droppedYear = 0,
            droppedQualifier = 0;

        foreach (var edge in edges)
        {
            // Drop unresolved targets (link didn't match any page)
            if (edge.ToId == 0)
            {
                droppedUnresolved++;
                continue;
            }

            // Look up target type
            var targetType = nodeTypeMap.GetValueOrDefault(edge.ToId, "");
            edge.ToType = targetType;

            // Drop edges to Year/Era entities — these are temporal metadata, not relationships
            if (targetType is "Year" or "Era")
            {
                droppedYear++;
                continue;
            }

            // Drop qualifier edges: TitleOrPosition targets on person-relationship labels
            if (targetType == "TitleOrPosition" && IsPersonRelationshipLabel(edge.Label))
            {
                droppedQualifier++;
                continue;
            }

            // Drop ForcePower/LightsaberForm targets on person-relationship labels
            if (
                targetType is "ForcePower" or "LightsaberForm"
                && IsPersonRelationshipLabel(edge.Label)
            )
            {
                droppedQualifier++;
                continue;
            }

            filteredEdges.Add(edge);
        }

        logger.LogInformation(
            "InfoboxGraph: edge filtering — dropped {Unresolved} unresolved, {Year} temporal, {Qualifier} qualifier → {Clean} clean edges",
            droppedUnresolved,
            droppedYear,
            droppedQualifier,
            filteredEdges.Count
        );

        // Write to MongoDB
        logger.LogInformation("InfoboxGraph: writing nodes...");
        if (nodes.Count > 0)
        {
            await _nodes.DeleteManyAsync(FilterDefinition<GraphNode>.Empty, ct);
            await _nodes.InsertManyAsync(nodes, new InsertManyOptions { IsOrdered = false }, ct);
        }

        logger.LogInformation("InfoboxGraph: writing edges...");
        if (filteredEdges.Count > 0)
        {
            await _edges.DeleteManyAsync(FilterDefinition<RelationshipEdge>.Empty, ct);
            await _edges.InsertManyAsync(
                filteredEdges,
                new InsertManyOptions { IsOrdered = false },
                ct
            );
        }

        // Create indexes
        await _nodes.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<GraphNode>(
                    Builders<GraphNode>.IndexKeys.Ascending(n => n.Type)
                ),
                new CreateIndexModel<GraphNode>(
                    Builders<GraphNode>.IndexKeys.Ascending(n => n.Name)
                ),
                new CreateIndexModel<GraphNode>(
                    Builders<GraphNode>.IndexKeys.Ascending(n => n.Continuity)
                ),
                new CreateIndexModel<GraphNode>(
                    Builders<GraphNode>
                        .IndexKeys.Ascending("temporalFacets.semantic")
                        .Ascending("temporalFacets.year"),
                    new CreateIndexOptions { Name = "ix_temporal_semantic_year" }
                ),
                new CreateIndexModel<GraphNode>(
                    Builders<GraphNode>
                        .IndexKeys.Ascending("temporalFacets.calendar")
                        .Ascending("temporalFacets.year"),
                    new CreateIndexOptions { Name = "ix_temporal_calendar_year" }
                ),
            ],
            ct
        );

        await _edges.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<RelationshipEdge>(
                    Builders<RelationshipEdge>.IndexKeys.Ascending(e => e.FromId)
                ),
                new CreateIndexModel<RelationshipEdge>(
                    Builders<RelationshipEdge>.IndexKeys.Ascending(e => e.ToId)
                ),
                new CreateIndexModel<RelationshipEdge>(
                    Builders<RelationshipEdge>.IndexKeys.Ascending(e => e.Label)
                ),
                new CreateIndexModel<RelationshipEdge>(
                    Builders<RelationshipEdge>.IndexKeys.Ascending(e => e.Continuity)
                ),
                new CreateIndexModel<RelationshipEdge>(
                    Builders<RelationshipEdge>
                        .IndexKeys.Ascending(e => e.FromId)
                        .Ascending(e => e.Label)
                ),
            ],
            ct
        );

        logger.LogInformation(
            "InfoboxGraph: complete. {Nodes} nodes, {Edges} edges (from {RawEdges} raw), indexes created.",
            nodes.Count,
            filteredEdges.Count,
            edges.Count
        );
    }

    /// <summary>
    /// Build a lookup from wiki URL AND title → PageId so we can resolve link targets.
    /// Infobox links may use URLs that don't exactly match the page's stored wikiUrl
    /// (redirects, disambiguation, URL encoding differences), so we also match by title.
    /// </summary>
    async Task<Dictionary<string, int>> BuildWikiUrlLookupAsync(CancellationToken ct)
    {
        var lookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var cursor = await _pages
            .Find(FilterDefinition<Page>.Empty)
            .Project(
                Builders<Page>
                    .Projection.Include(p => p.PageId)
                    .Include(p => p.Title)
                    .Include(p => p.WikiUrl)
            )
            .ToCursorAsync(ct);

        while (await cursor.MoveNextAsync(ct))
        {
            foreach (var doc in cursor.Current)
            {
                var pageId = doc["_id"].AsInt32;
                var wikiUrl = doc.Contains("wikiUrl") ? doc["wikiUrl"].AsString : null;
                var title = doc.Contains("title") ? doc["title"].AsString : null;

                // Primary: exact URL match
                if (wikiUrl is not null)
                    lookup.TryAdd(wikiUrl, pageId);

                // Secondary: match by title (handles redirects and disambiguation)
                if (title is not null)
                    lookup.TryAdd(title, pageId);
            }
        }

        return lookup;
    }

    /// <summary>
    /// Resolve a link target to a PageId. Tries URL first, then link content text as title.
    /// </summary>
    int ResolveLinkTarget(string? href, string? content, Dictionary<string, int> lookup)
    {
        // Try exact URL match
        if (href is not null && lookup.TryGetValue(href, out var byUrl))
            return byUrl;

        // Try link display text as title
        if (content is not null && lookup.TryGetValue(content, out var byContent))
            return byContent;

        // Try extracting title from URL (decode /wiki/Title_Name)
        if (href is not null && href.Contains("/wiki/"))
        {
            var titleFromUrl = Uri.UnescapeDataString(href[(href.LastIndexOf("/wiki/") + 6)..])
                .Replace('_', ' ');
            if (lookup.TryGetValue(titleFromUrl, out var byExtracted))
                return byExtracted;
        }

        return 0; // unresolved
    }

    /// <summary>
    /// Match each Value string to its primary link from the link lookup.
    /// The primary entity is the text before the first '(' in the Value.
    /// Parenthetical text is treated as qualifier metadata, and temporal bounds are parsed from it.
    /// </summary>
    static List<(
        BsonDocument link,
        string? qualifier,
        int? fromYear,
        int? toYear
    )> ExtractPrimaryLinks(List<string> values, Dictionary<string, BsonDocument> linkLookup)
    {
        var results =
            new List<(BsonDocument link, string? qualifier, int? fromYear, int? toYear)>();
        if (values.Count == 0 || linkLookup.Count == 0)
            return results;

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            // Extract primary name: text before first '('
            var parenIdx = value.IndexOf('(');
            var primaryName = (parenIdx > 0 ? value[..parenIdx] : value).Trim().TrimEnd(',');

            // Extract qualifier: text inside parentheses
            string? qualifier = null;
            int? fromYear = null;
            int? toYear = null;

            if (parenIdx > 0)
            {
                var closeIdx = value.LastIndexOf(')');
                if (closeIdx > parenIdx)
                {
                    qualifier = value[(parenIdx + 1)..closeIdx].Trim();

                    // Parse temporal bounds from qualifier: "19 BBY–4 ABY", "5 ABY, officially"
                    var yearMatches = Regex.Matches(
                        qualifier,
                        @"(\d[\d,]*)\s*(BBY|ABY)",
                        RegexOptions.IgnoreCase
                    );
                    if (yearMatches.Count >= 1)
                    {
                        var y1 = ParseGalacticYear(yearMatches[0].Value);
                        fromYear = y1;
                    }
                    if (yearMatches.Count >= 2)
                    {
                        var y2 = ParseGalacticYear(yearMatches[1].Value);
                        toYear = y2;
                    }
                }
            }

            // Try to match primary name to a link (case-insensitive)
            if (linkLookup.TryGetValue(primaryName, out var matchedLink))
            {
                results.Add((matchedLink, qualifier, fromYear, toYear));
            }
            else
            {
                // Fallback: try matching the full value text (for simple cases without parens)
                if (linkLookup.TryGetValue(value.Trim(), out var fullMatch))
                {
                    results.Add((fullMatch, null, null, null));
                }
                // Last resort: if Value starts with a link content, use it
                else
                {
                    foreach (var (linkContent, linkDoc) in linkLookup)
                    {
                        if (
                            primaryName.StartsWith(linkContent, StringComparison.OrdinalIgnoreCase)
                            || linkContent.StartsWith(
                                primaryName,
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                        {
                            results.Add((linkDoc, qualifier, fromYear, toYear));
                            break;
                        }
                    }
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Edge labels that imply the target should be a person/character, not a title or rank.
    /// Used to filter qualifier noise (e.g. apprentice_of → "Jedi Master" instead of the actual person).
    /// </summary>
    static bool IsPersonRelationshipLabel(string label) =>
        label
            is "apprentice_of"
                or "master_of"
                or "led_by"
                or "commanded_by"
                or "head_of_state"
                or "head_of_government"
                or "commander_in_chief"
                or "child_of"
                or "parent_of"
                or "sibling_of"
                or "partner_of"
                or "owned_by"
                or "founded_by"
                or "created_by"
                or "authored_by"
                or "written_by"
                or "illustrated_by"
                or "narrated_by"
                or "directed_by"
                or "organized_by"
                or "hosted_by"
                or "has_notable_member"
                or "has_competitor";

    /// <summary>
    /// Parse a date text like "22 BBY", "c. 5100 BBY", "19 BBY, Felucia" into a sort-key year.
    /// Returns negative for BBY, positive for ABY, null if unparseable.
    /// </summary>
    static int? ParseGalacticYear(string text)
    {
        var match = Regex.Match(text, @"(\d[\d,]*)\s*(BBY|ABY)", RegexOptions.IgnoreCase);
        if (!match.Success)
            return null;
        if (!int.TryParse(match.Groups[1].Value.Replace(",", ""), out var num))
            return null;
        return match.Groups[2].Value.Equals("BBY", StringComparison.OrdinalIgnoreCase) ? -num : num;
    }

    /// <summary>
    /// Parse a real-world date text like "October 23, 1959", "September 2017", "2015" into a CE year.
    /// </summary>
    static int? ParseRealWorldYear(string text)
    {
        // "October 23, 1959" or "November 19, 1983 in" — extract 4-digit year
        var match = Regex.Match(text, @"\b(1[89]\d{2}|20[0-3]\d)\b");
        return match.Success && int.TryParse(match.Value, out var year) ? year : null;
    }

    /// <summary>
    /// Detect calendar system and parse year from date text.
    /// Tries galactic (BBY/ABY) first, then real-world, then unknown.
    /// </summary>
    static (string calendar, int? year) DetectCalendarAndParse(string text, string calendarHint)
    {
        if (calendarHint == "galactic")
        {
            var gy = ParseGalacticYear(text);
            return ("galactic", gy); // null year is fine — vague galactic date
        }

        if (calendarHint == "real")
        {
            var ry = ParseRealWorldYear(text);
            return ("real", ry);
        }

        // "auto" — try galactic first (unambiguous), then real-world
        var galactic = ParseGalacticYear(text);
        if (galactic.HasValue)
            return ("galactic", galactic);

        var real = ParseRealWorldYear(text);
        if (real.HasValue)
            return ("real", real);

        return ("unknown", null);
    }

    /// <summary>
    /// Assign Order values to facets within each semantic dimension prefix.
    /// Groups by dimension (e.g. "institutional"), sorts by year (nulls last), assigns 0-based order.
    /// </summary>
    static void AssignFacetOrder(List<TemporalFacet> facets)
    {
        var groups = facets.GroupBy(f => f.Semantic.Split('.')[0]);
        foreach (var group in groups)
        {
            var ordered = group
                .OrderBy(f => f.Year.HasValue ? 0 : 1) // year-bearing first
                .ThenBy(f => f.Year ?? int.MaxValue)
                .ToList();
            for (var i = 0; i < ordered.Count; i++)
                ordered[i].Order = i;
        }
    }

    /// <summary>
    /// Convert an infobox field label into a snake_case edge label.
    /// </summary>
    static string NormaliseLabel(string label)
    {
        // Remove parenthetical suffixes like "(s)" and clean up
        var clean = label.Replace("(s)", "").Replace("(", "").Replace(")", "").Trim();

        // Convert to snake_case
        var result = new System.Text.StringBuilder();
        for (var i = 0; i < clean.Length; i++)
        {
            var c = clean[i];
            if (c == ' ' || c == '-' || c == '/')
            {
                result.Append('_');
            }
            else if (char.IsUpper(c) && i > 0 && !char.IsUpper(clean[i - 1]))
            {
                result.Append('_');
                result.Append(char.ToLowerInvariant(c));
            }
            else
            {
                result.Append(char.ToLowerInvariant(c));
            }
        }

        return result.ToString().Trim('_');
    }
}
