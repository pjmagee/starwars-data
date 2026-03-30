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
    ILogger<InfoboxGraphService> logger)
{
    readonly IMongoCollection<Page> _pages = mongoClient
        .GetDatabase(settings.Value.PagesDb).GetCollection<Page>("Pages");
    readonly IMongoCollection<GraphNode> _nodes = mongoClient
        .GetDatabase(settings.Value.RelationshipGraphDb).GetCollection<GraphNode>("nodes");
    readonly IMongoCollection<RelationshipEdge> _edges = mongoClient
        .GetDatabase(settings.Value.RelationshipGraphDb).GetCollection<RelationshipEdge>("edges_v2");

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
        "Titles", "Pronouns",

        // Physical attributes (links go to color/unit pages, not entities)
        "Height", "Mass", "Eye color", "Hair color", "Skin color", "Feather color",
        "Plating color", "Sensor color", "Color",
        "Average height", "Average length", "Average mass", "Average lifespan", "Average wingspan",

        // Descriptions / classifications
        "Class", "Designation", "Classification", "Distinctions", "Demonym",
        "Organization type", "Model", "Shape", "Purpose",

        // Measurements / specs
        "Length", "Width", "Height/depth", "Diameter", "Weight",
        "Hyperdrive rating", "Maximum atmospheric speed", "Megalight per hour",
        "Maximum altitude", "Maximum speed", "Maximum depth",
        "Passengers", "Population", "Consumables", "Cargo capacity",
        "Orbital position", "Orbital period", "Rotation period", "Surface water",

        // Publication / media metadata
        "Pages", "Media type", "Issue number", "Issue #", "Issues", "Format",
        "Run time", "Number of players", "Ages of players", "Playing time",
        "Number of books", "Number of audiobooks", "Collected issues",
        "Skills required",

        // Dates as text (stored as property, temporal edges derived separately)
        "Date", "Date established", "Date dissolved", "Date reorganized",
        "Date restored", "Date fragmented", "Date founded", "Beginning", "End",
        "Constructed", "Destroyed",

        // Outcomes / descriptions
        "Outcome", "Result", "Diet", "Habitat", "Atmosphere", "Terrain", "Climate",
        "Grid square", "Grid coordinates",

        // Misc scalar
        "Symptoms", "Transmission type", "Incubation period",
        "Number infected", "Number killed",
        "Other markings", "Other systems",
        "Major exports", "Major imports",
    };

    /// <summary>
    /// Fields that are ALWAYS relationships — their linked targets are meaningful entities.
    /// Maps field name → edge label for the relationship.
    /// </summary>
    static readonly Dictionary<string, string> RelationshipLabels = new(StringComparer.OrdinalIgnoreCase)
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
        ["Born"] = "born_at",
        ["Died"] = "died_at",
        ["Cybernetics"] = "has_cybernetics",
        ["Gender"] = "gender",
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
        ["Date created"] = "created_on",
        ["Date destroyed"] = "destroyed_on",
        ["Date engineered"] = "engineered_on",
        ["Founding"] = "founded_on",
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
        logger.LogInformation("InfoboxGraph: {Count} wiki URL → PageId mappings", wikiUrlToPageId.Count);

        // Process all pages with infoboxes
        var filter = Builders<Page>.Filter.Ne(p => p.Infobox, null);
        var totalPages = await _pages.CountDocumentsAsync(filter, cancellationToken: ct);
        logger.LogInformation("InfoboxGraph: {Total} pages with infoboxes to process", totalPages);

        var cursor = await _pages.Find(filter)
            .Project(Builders<Page>.Projection
                .Include(p => p.PageId)
                .Include(p => p.Title)
                .Include(p => p.Infobox)
                .Include(p => p.WikiUrl)
                .Include(p => p.Continuity)
                .Include(p => p.ContentHash))
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
                    ? Enum.TryParse<Continuity>(doc["continuity"].AsString, out var c) ? c : Continuity.Unknown
                    : Continuity.Unknown;
                var contentHash = doc.Contains("contentHash") && !doc["contentHash"].IsBsonNull
                    ? doc["contentHash"].AsString : null;
                var wikiUrl = doc.Contains("wikiUrl") ? doc["wikiUrl"].AsString : null;

                var infoboxDoc = doc["infobox"].AsBsonDocument;
                var template = infoboxDoc.Contains("Template") && !infoboxDoc["Template"].IsBsonNull
                    ? infoboxDoc["Template"].AsString : null;
                var imageUrl = infoboxDoc.Contains("ImageUrl") && !infoboxDoc["ImageUrl"].IsBsonNull
                    ? infoboxDoc["ImageUrl"].AsString : null;

                // Extract type from template URL
                var type = "Unknown";
                if (template is not null)
                {
                    var idx = template.LastIndexOf(':');
                    if (idx >= 0) type = template[(idx + 1)..];
                }

                // Parse infobox data items
                var dataItems = infoboxDoc.Contains("Data") && infoboxDoc["Data"].IsBsonArray
                    ? infoboxDoc["Data"].AsBsonArray : new BsonArray();

                var properties = new Dictionary<string, List<string>>();

                foreach (var item in dataItems)
                {
                    if (item is not BsonDocument itemDoc) continue;
                    var label = itemDoc.Contains("Label") ? itemDoc["Label"].AsString : null;
                    if (label is null) continue;

                    var values = itemDoc.Contains("Values") && itemDoc["Values"].IsBsonArray
                        ? itemDoc["Values"].AsBsonArray.Select(v => v.AsString).ToList()
                        : [];
                    var links = itemDoc.Contains("Links") && itemDoc["Links"].IsBsonArray
                        ? itemDoc["Links"].AsBsonArray
                            .Where(l => l is BsonDocument)
                            .Select(l => l.AsBsonDocument)
                            .ToList()
                        : [];

                    // Classify: property or relationship?
                    if (AlwaysProperties.Contains(label))
                    {
                        // Store as scalar property
                        if (values.Count > 0)
                            properties[label] = values;
                    }
                    else if (RelationshipLabels.TryGetValue(label, out var edgeLabel))
                    {
                        // Create edges for each link target
                        foreach (var link in links)
                        {
                            var href = link.Contains("Href") ? link["Href"].AsString : null;
                            var content = link.Contains("Content") ? link["Content"].AsString : null;
                            if (href is null || content is null) continue;

                            // Resolve the link target to a PageId
                            var targetPageId = wikiUrlToPageId.GetValueOrDefault(href);

                            edges.Add(new RelationshipEdge
                            {
                                FromId = pageId,
                                FromName = title,
                                FromType = type,
                                ToId = targetPageId,
                                ToName = content,
                                ToType = "", // filled when target node is processed
                                Label = edgeLabel,
                                Weight = 1.0,
                                Evidence = $"Infobox field '{label}'",
                                SourcePageId = pageId,
                                Continuity = continuity,
                            });
                        }

                        // If no links but has values, treat as property fallback
                        if (links.Count == 0 && values.Count > 0)
                            properties[label] = values;
                    }
                    else
                    {
                        // Unknown field — use heuristic: if it has links, create relationships;
                        // otherwise store as property
                        if (links.Count > 0)
                        {
                            var inferredLabel = NormaliseLabel(label);
                            foreach (var link in links)
                            {
                                var href = link.Contains("Href") ? link["Href"].AsString : null;
                                var content = link.Contains("Content") ? link["Content"].AsString : null;
                                if (href is null || content is null) continue;

                                var targetPageId = wikiUrlToPageId.GetValueOrDefault(href);
                                edges.Add(new RelationshipEdge
                                {
                                    FromId = pageId,
                                    FromName = title,
                                    FromType = type,
                                    ToId = targetPageId,
                                    ToName = content,
                                    ToType = "",
                                    Label = inferredLabel,
                                    Weight = 0.8, // lower confidence for inferred labels
                                    Evidence = $"Infobox field '{label}' (inferred)",
                                    SourcePageId = pageId,
                                    Continuity = continuity,
                                });
                            }
                        }
                        else if (values.Count > 0)
                        {
                            properties[label] = values;
                        }
                    }
                }

                nodes.Add(new GraphNode
                {
                    PageId = pageId,
                    Name = title,
                    Type = type,
                    Continuity = continuity,
                    Properties = properties,
                    ImageUrl = imageUrl,
                    WikiUrl = wikiUrl,
                    ContentHash = contentHash,
                    ProcessedAt = DateTime.UtcNow,
                });

                processed++;
                if (processed % 10000 == 0)
                    logger.LogInformation("InfoboxGraph: processed {Count}/{Total} pages", processed, totalPages);
            }
        }

        logger.LogInformation("InfoboxGraph: processed {Count} pages → {Nodes} nodes, {Edges} edges",
            processed, nodes.Count, edges.Count);

        // Write to MongoDB
        logger.LogInformation("InfoboxGraph: writing nodes...");
        if (nodes.Count > 0)
        {
            await _nodes.DeleteManyAsync(FilterDefinition<GraphNode>.Empty, ct);
            await _nodes.InsertManyAsync(nodes, new InsertManyOptions { IsOrdered = false }, ct);
        }

        logger.LogInformation("InfoboxGraph: writing edges...");
        if (edges.Count > 0)
        {
            await _edges.DeleteManyAsync(FilterDefinition<RelationshipEdge>.Empty, ct);
            await _edges.InsertManyAsync(edges, new InsertManyOptions { IsOrdered = false }, ct);
        }

        // Create indexes
        await _nodes.Indexes.CreateManyAsync([
            new CreateIndexModel<GraphNode>(Builders<GraphNode>.IndexKeys.Ascending(n => n.Type)),
            new CreateIndexModel<GraphNode>(Builders<GraphNode>.IndexKeys.Ascending(n => n.Name)),
            new CreateIndexModel<GraphNode>(Builders<GraphNode>.IndexKeys.Ascending(n => n.Continuity)),
        ], ct);

        await _edges.Indexes.CreateManyAsync([
            new CreateIndexModel<RelationshipEdge>(Builders<RelationshipEdge>.IndexKeys.Ascending(e => e.FromId)),
            new CreateIndexModel<RelationshipEdge>(Builders<RelationshipEdge>.IndexKeys.Ascending(e => e.ToId)),
            new CreateIndexModel<RelationshipEdge>(Builders<RelationshipEdge>.IndexKeys.Ascending(e => e.Label)),
            new CreateIndexModel<RelationshipEdge>(Builders<RelationshipEdge>.IndexKeys.Ascending(e => e.Continuity)),
            new CreateIndexModel<RelationshipEdge>(
                Builders<RelationshipEdge>.IndexKeys
                    .Ascending(e => e.FromId)
                    .Ascending(e => e.Label)),
        ], ct);

        logger.LogInformation("InfoboxGraph: complete. {Nodes} nodes, {Edges} edges, indexes created.",
            nodes.Count, edges.Count);
    }

    /// <summary>
    /// Build a lookup from wiki URL → PageId so we can resolve link targets to graph nodes.
    /// </summary>
    async Task<Dictionary<string, int>> BuildWikiUrlLookupAsync(CancellationToken ct)
    {
        var lookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var cursor = await _pages.Find(FilterDefinition<Page>.Empty)
            .Project(Builders<Page>.Projection
                .Include(p => p.PageId)
                .Include(p => p.WikiUrl))
            .ToCursorAsync(ct);

        while (await cursor.MoveNextAsync(ct))
        {
            foreach (var doc in cursor.Current)
            {
                var pageId = doc["_id"].AsInt32;
                var wikiUrl = doc.Contains("wikiUrl") ? doc["wikiUrl"].AsString : null;
                if (wikiUrl is not null)
                    lookup.TryAdd(wikiUrl, pageId);
            }
        }

        return lookup;
    }

    /// <summary>
    /// Convert an infobox field label into a snake_case edge label.
    /// </summary>
    static string NormaliseLabel(string label)
    {
        // Remove parenthetical suffixes like "(s)" and clean up
        var clean = label
            .Replace("(s)", "")
            .Replace("(", "")
            .Replace(")", "")
            .Trim();

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
