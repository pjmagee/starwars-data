using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries;
using TimelineEvent = StarWarsData.Models.Entities.TimelineEvent;

namespace StarWarsData.Services;

/// <summary>
/// ETL that populates the <c>timeline.{TemplateName}</c> collections from the knowledge graph.
/// Sole writer of these collections: earlier revisions had a text-regex transformer that
/// walked infobox hyperlinks directly, which was removed in favour of this calendar-aware path.
///
/// <para>
/// Source of truth is <see cref="GraphNode.TemporalFacets"/>. Both galactic (BBY/ABY) and
/// real-world (CE) facets with a parsed year are emitted, tagged with the corresponding
/// <see cref="TimelineEvent.Calendar"/>. Downstream the <see cref="TimelineService"/> filters
/// by calendar so the two modes never visually mix unless explicitly asked to.
/// The display-side <see cref="TimelineEvent.Properties"/> payload — rendered as clickable
/// links in the frontend info panel — is copied verbatim from the source <see cref="Page.Infobox"/>
/// via a batched lookup, so the UI keeps every hyperlink it has today.
/// </para>
///
/// <para>
/// Multi-facet entities produce multiple rows: e.g. a war with <c>conflict.start=-22</c>
/// and <c>conflict.end=-19</c> emits two <see cref="TimelineEvent"/> rows, matching the
/// current per-year-link denormalisation that the Timeline page expects.
/// </para>
/// </summary>
public class KgTimelineBuilderService(IMongoClient mongoClient, IOptions<SettingsOptions> settings, ILogger<KgTimelineBuilderService> logger)
{
    const string GalacticCalendar = "galactic";
    const string RealCalendar = "real";
    const int PageBatchSize = 500;

    readonly IMongoDatabase _db = mongoClient.GetDatabase(settings.Value.DatabaseName);

    readonly IMongoCollection<GraphNode> _nodes = mongoClient.GetDatabase(settings.Value.DatabaseName).GetCollection<GraphNode>(Collections.KgNodes);

    readonly IMongoCollection<Page> _pages = mongoClient.GetDatabase(settings.Value.DatabaseName).GetCollection<Page>(Collections.Pages);

    /// <summary>
    /// Rebuilds every <c>timeline.{TemplateName}</c> collection from the current KG state.
    /// Delete-and-replace per template; write order is stable so consumers see a brief
    /// rebuild window only on the template being replaced.
    /// </summary>
    public async Task BuildAsync(CancellationToken ct = default)
    {
        logger.LogInformation("KG-backed timeline ETL: starting");

        // ── Step 1: Load every KG node that has at least one emittable temporal facet
        // (galactic OR real). Any node without such a facet can't produce timeline rows, so
        // filtering at the DB saves both transport and memory.
        var elemMatch = Builders<GraphNode>.Filter.ElemMatch(n => n.TemporalFacets, Builders<TemporalFacet>.Filter.In(f => f.Calendar, new[] { GalacticCalendar, RealCalendar }));
        var nodes = await _nodes.Find(elemMatch).ToListAsync(ct);
        logger.LogInformation("KG timeline ETL: loaded {Count} nodes with temporal facets", nodes.Count);

        // ── Step 2: Batch-fetch the source Page documents for each KG node so we can copy
        // the original Infobox.Data into TimelineEvent.Properties for the info panel render.
        var pageIds = nodes.Select(n => n.PageId).Distinct().ToList();
        var infoboxByPageId = new Dictionary<int, List<InfoboxProperty>>(pageIds.Count);

        for (var i = 0; i < pageIds.Count; i += PageBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = pageIds.Skip(i).Take(PageBatchSize).ToList();
            var filter = Builders<Page>.Filter.In(p => p.PageId, batch);
            var projection = Builders<Page>.Projection.Include(p => p.PageId).Include(p => p.Infobox);

            var pagesInBatch = await _pages.Find(filter).Project<Page>(projection).ToListAsync(ct);

            foreach (var p in pagesInBatch)
            {
                if (p.Infobox?.Data is { Count: > 0 } data)
                    infoboxByPageId[p.PageId] = data;
            }
        }
        logger.LogInformation("KG timeline ETL: loaded infobox payloads for {Count}/{Total} pages", infoboxByPageId.Count, pageIds.Count);

        // ── Step 3: Project nodes × galactic facets → TimelineEvent rows, grouped by Type.
        var eventsByTemplate = new Dictionary<string, List<TimelineEvent>>(StringComparer.Ordinal);

        foreach (var node in nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Type))
                continue;

            infoboxByPageId.TryGetValue(node.PageId, out var infoboxProperties);

            foreach (var facet in node.TemporalFacets)
            {
                if (!IsEmittable(facet))
                    continue;

                var isGalactic = string.Equals(facet.Calendar, GalacticCalendar, StringComparison.OrdinalIgnoreCase);

                TimelineEvent evt;
                if (isGalactic)
                {
                    // Galactic storage convention: Year is the POSITIVE magnitude and
                    // Demarcation carries the sign. Facet.Year is a sort-key (negative=BBY)
                    // so split it back out here.
                    var sortKey = facet.Year!.Value;
                    var demarcation = sortKey < 0 ? Demarcation.Bby : Demarcation.Aby;
                    var magnitude = Math.Abs(sortKey);

                    evt = new TimelineEvent
                    {
                        Title = node.Name,
                        Template = node.Type,
                        TemplateUri = $"{Collections.TemplateUrlPrefix}{node.Type}",
                        ImageUrl = node.ImageUrl,
                        Calendar = Calendar.Galactic,
                        Year = magnitude,
                        Demarcation = demarcation,
                        DateEvent = NormaliseDateEvent(facet.Field),
                        Properties = infoboxProperties ?? [],
                        Continuity = node.Continuity,
                        Realm = node.Realm,
                        PageId = node.PageId,
                        WikiUrl = node.WikiUrl,
                    };
                }
                else
                {
                    // Real-world storage: RealYear is the signed CE year (negative=BCE).
                    // Year / Demarcation are left unset so galactic-mode $expr filters
                    // naturally skip these rows, and vice versa for real-mode filters.
                    evt = new TimelineEvent
                    {
                        Title = node.Name,
                        Template = node.Type,
                        TemplateUri = $"{Collections.TemplateUrlPrefix}{node.Type}",
                        ImageUrl = node.ImageUrl,
                        Calendar = Calendar.Real,
                        RealYear = facet.Year!.Value,
                        DateEvent = NormaliseDateEvent(facet.Field),
                        Properties = infoboxProperties ?? [],
                        Continuity = node.Continuity,
                        Realm = node.Realm,
                        PageId = node.PageId,
                        WikiUrl = node.WikiUrl,
                    };
                }

                if (!eventsByTemplate.TryGetValue(node.Type, out var list))
                {
                    list = [];
                    eventsByTemplate[node.Type] = list;
                }
                list.Add(evt);
            }
        }

        // ── Step 4: Replace each timeline.{Template} collection. Delete-and-insert is the
        // simplest atomic-per-template option and the windows are short (a few hundred ms
        // per template even for the largest categories).
        var totalRows = 0;
        foreach (var (template, events) in eventsByTemplate.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var collectionName = Collections.TimelinePrefix + template;
            var collection = _db.GetCollection<TimelineEvent>(collectionName);

            await collection.DeleteManyAsync(FilterDefinition<TimelineEvent>.Empty, ct);
            if (events.Count > 0)
                await collection.InsertManyAsync(events, cancellationToken: ct);

            totalRows += events.Count;
            logger.LogInformation("KG timeline ETL: wrote {Count} rows to {Collection}", events.Count, collectionName);
        }

        // ── Step 5: Drop stale per-template collections that no longer exist in the KG.
        // Keeps the read-path's `GetTimelineCategories` list accurate after entity removals.
        var existingCollectionsCursor = await _db.ListCollectionNamesAsync(cancellationToken: ct);
        var existingCollections = (await existingCollectionsCursor.ToListAsync(ct)).Where(n => n.StartsWith(Collections.TimelinePrefix, StringComparison.Ordinal)).ToList();

        var liveCollectionNames = eventsByTemplate.Keys.Select(t => Collections.TimelinePrefix + t).ToHashSet(StringComparer.Ordinal);

        foreach (var stale in existingCollections.Where(c => !liveCollectionNames.Contains(c)))
        {
            await _db.DropCollectionAsync(stale, ct);
            logger.LogInformation("KG timeline ETL: dropped stale collection {Collection}", stale);
        }

        logger.LogInformation("KG timeline ETL: complete — {Templates} templates, {Rows} rows total", eventsByTemplate.Count, totalRows);
    }

    /// <summary>
    /// A facet contributes a timeline row when it has a parsed year and a known calendar
    /// (galactic BBY/ABY or real-world CE). Vague text facets (e.g. "During the Clone Wars")
    /// and unknown calendars fall out here.
    /// </summary>
    static bool IsEmittable(TemporalFacet facet) =>
        facet.Year.HasValue && (string.Equals(facet.Calendar, GalacticCalendar, StringComparison.OrdinalIgnoreCase) || string.Equals(facet.Calendar, RealCalendar, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Mirrors the pre-KG transformer: a facet sourced from a plain "Date" infobox field
    /// collapses to an empty label so the frontend renders the title without a "Date:" prefix.
    /// Any other field keeps its label ("Born", "Founded", "Date established", …).
    /// </summary>
    static string NormaliseDateEvent(string? field) => string.Equals(field, InfoboxFieldLabels.Date, StringComparison.OrdinalIgnoreCase) ? string.Empty : field ?? string.Empty;
}
