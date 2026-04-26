using System.Text;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using StarWarsData.Models.Entities;
using StarWarsData.Services.KnowledgeGraph.Definitions;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders;

/// <summary>
/// Default <see cref="INodeBuilder"/> implementation. Provides the full generic
/// field-classification loop (temporal facets, scalar properties, relationship
/// edges) plus shared utilities used by every concrete builder. Per-type builders
/// subclass this base and override <see cref="NodeType"/> to declare the type they
/// handle, then optionally override the protected hooks (<see cref="OnRelationshipExtracted"/>,
/// <see cref="OnFinalize"/>, etc.) to inject type-specific behaviour without
/// duplicating the generic loop.
///
/// See <c>eng/design/007-kg-per-type-builders.md</c> for the design rationale.
/// </summary>
public abstract partial class NodeBuilderBase : INodeBuilder
{
    /// <summary>The KG node type this builder handles.</summary>
    public abstract string NodeType { get; }

    public virtual NodeBuilderResult Build(NodeBuilderContext ctx)
    {
        var properties = new Dictionary<string, List<string>>();
        var facets = new List<TemporalFacet>();
        var edges = new List<RelationshipEdge>();

        foreach (var item in ctx.DataItems)
        {
            if (item is not BsonDocument itemDoc)
                continue;
            var label = itemDoc.Contains(InfoboxBsonFields.Label) ? itemDoc[InfoboxBsonFields.Label].AsString : null;
            if (label is null)
                continue;

            var values =
                itemDoc.Contains(InfoboxBsonFields.Values) && itemDoc[InfoboxBsonFields.Values].IsBsonArray ? itemDoc[InfoboxBsonFields.Values].AsBsonArray.Select(v => v.AsString).ToList() : [];
            var links =
                itemDoc.Contains(InfoboxBsonFields.Links) && itemDoc[InfoboxBsonFields.Links].IsBsonArray
                    ? itemDoc[InfoboxBsonFields.Links].AsBsonArray.Where(l => l is BsonDocument).Select(l => l.AsBsonDocument).ToList()
                    : [];

            // Temporal fields are classified BEFORE relationships. Links inside a temporal
            // value are contextual (place of death, killer, etc.) and must NOT produce
            // semantic edges — e.g. Character.Died = "4 ABY, Death Star II over Endor"
            // produces a lifespan.end facet but not a "died → Death Star II" edge.
            if (ctx.Definition.TemporalFields.TryGetValue(label, out var temporalDef))
            {
                ProcessTemporalField(label, values, temporalDef, facets);
                if (values.Count > 0)
                    properties[label] = values;
                continue;
            }

            var hasLabelDef = ctx.Definition.Relationships.TryGetValue(label, out var labelDef);
            if (ctx.Definition.Properties.Contains(label))
            {
                if (values.Count > 0)
                    properties[label] = values;
            }
            else if (!hasLabelDef && links.Count == 0)
            {
                // Fallthrough: no semantic classification anywhere. Preserve the raw text
                // so it isn't silently lost — see eng/design/013-kg-property-edge-duality.md.
                if (values.Count > 0)
                    properties[label] = values;
            }
            else if (hasLabelDef || links.Count > 0)
            {
                ProcessRelationshipField(ctx, label, labelDef, values, links, properties, edges);
            }
        }

        AssignFacetOrder(facets);

        var startFacets = facets.Where(f => f.Year.HasValue && (f.Semantic.EndsWith(".start") || f.Semantic.EndsWith(".point") || f.Semantic.EndsWith(".release"))).ToList();
        var endFacets = facets.Where(f => f.Year.HasValue && (f.Semantic.EndsWith(".end") || f.Semantic.EndsWith(".point"))).ToList();

        var startYear = startFacets.Count > 0 ? startFacets.Min(f => f.Year!.Value) : (int?)null;
        var endYear = endFacets.Count > 0 ? endFacets.Max(f => f.Year!.Value) : (int?)null;
        var startDateText = startFacets.FirstOrDefault()?.Text;
        var endDateText = endFacets.FirstOrDefault()?.Text;

        var node = new GraphNode
        {
            PageId = ctx.PageId,
            Name = ctx.Title,
            Type = ctx.Type,
            Continuity = ctx.Continuity,
            Realm = ctx.Realm,
            Properties = properties,
            ImageUrl = ctx.ImageUrl,
            WikiUrl = ctx.WikiUrl,
            StartYear = startYear,
            EndYear = endYear,
            StartDateText = startDateText,
            EndDateText = endDateText,
            TemporalFacets = facets,
            ContentHash = ctx.ContentHash,
            ProcessedAt = DateTime.UtcNow,
        };

        OnFinalize(ctx, node, edges);

        return new NodeBuilderResult(node, edges);
    }

    /// <summary>
    /// Final hook invoked after the node and edges have been fully assembled.
    /// Subclasses override to mutate either before they're handed to the
    /// coordinator (e.g. add derived properties, override start/end years).
    /// Default implementation is a no-op.
    /// </summary>
    protected virtual void OnFinalize(NodeBuilderContext ctx, GraphNode node, List<RelationshipEdge> edges) { }

    /// <summary>
    /// Extension point invoked once per relationship field after primary-link
    /// extraction. Default implementation is a no-op. Subclasses override this
    /// to emit type-specific properties without duplicating the generic loop.
    /// </summary>
    protected virtual void OnRelationshipExtracted(NodeBuilderContext ctx, string label, IReadOnlyList<PrimaryLink> primaryLinks, Dictionary<string, List<string>> properties) { }

    // ── Field processing ────────────────────────────────────────────────

    /// <summary>
    /// Emit one or more <see cref="TemporalFacet"/>s for a temporal-classified field.
    /// Range fields produce <c>.start</c> / <c>.end</c> / <c>.mid</c> facets per year;
    /// non-range fields produce a single facet per value.
    /// </summary>
    static void ProcessTemporalField(string label, List<string> values, TemporalFieldDefinition temporalDef, List<TemporalFacet> facets)
    {
        foreach (var val in values)
        {
            if (string.IsNullOrWhiteSpace(val))
                continue;

            if (temporalDef.IsRange)
            {
                var years = ParseAllGalacticYears(val);
                if (years.Count == 0)
                {
                    facets.Add(
                        new TemporalFacet
                        {
                            Field = label,
                            Semantic = $"{temporalDef.Semantic}.point",
                            Calendar = "galactic",
                            Year = null,
                            Text = val,
                        }
                    );
                }
                else if (years.Count == 1)
                {
                    facets.Add(
                        new TemporalFacet
                        {
                            Field = label,
                            Semantic = $"{temporalDef.Semantic}.point",
                            Calendar = "galactic",
                            Year = years[0],
                            Text = val,
                        }
                    );
                }
                else
                {
                    for (var i = 0; i < years.Count; i++)
                    {
                        var role =
                            i == 0 ? "start"
                            : i == years.Count - 1 ? "end"
                            : "mid";
                        facets.Add(
                            new TemporalFacet
                            {
                                Field = label,
                                Semantic = $"{temporalDef.Semantic}.{role}",
                                Calendar = "galactic",
                                Year = years[i],
                                Text = val,
                            }
                        );
                    }
                }
            }
            else
            {
                var (calendar, year) = DetectCalendarAndParse(val, temporalDef.Calendar);
                facets.Add(
                    new TemporalFacet
                    {
                        Field = label,
                        Semantic = temporalDef.Semantic,
                        Calendar = calendar,
                        Year = year,
                        Text = val,
                    }
                );
            }
        }
    }

    /// <summary>
    /// Resolve relationship-typed fields into outbound edges. Calls
    /// <see cref="OnRelationshipExtracted"/> after primary-link extraction so
    /// subclasses can emit additional type-specific properties (e.g. ordered
    /// waypoint sequences for trade routes).
    /// </summary>
    void ProcessRelationshipField(
        NodeBuilderContext ctx,
        string label,
        LabelDefinition? labelDef,
        List<string> values,
        List<BsonDocument> links,
        Dictionary<string, List<string>> properties,
        List<RelationshipEdge> edges
    )
    {
        var edgeLabel = labelDef?.Label ?? NormaliseLabel(label);
        var weight = labelDef?.Weight ?? 0.8;

        var linkLookup = new Dictionary<string, BsonDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in links)
        {
            var content = link.Contains(InfoboxBsonFields.Content) ? link[InfoboxBsonFields.Content].AsString : null;
            if (content is not null)
                linkLookup.TryAdd(content, link);
        }

        var primaryLinks = ExtractPrimaryLinks(values, linkLookup);

        if (primaryLinks.Count > 0)
        {
            foreach (var pl in primaryLinks)
            {
                var href = pl.Link.Contains(InfoboxBsonFields.Href) ? pl.Link[InfoboxBsonFields.Href].AsString : null;
                var content = pl.Link.Contains(InfoboxBsonFields.Content) ? pl.Link[InfoboxBsonFields.Content].AsString : null;
                if (href is null || content is null)
                    continue;

                var targetPageId = ResolveLinkTarget(href, content, ctx.WikiUrlToPageId);

                edges.Add(
                    new RelationshipEdge
                    {
                        FromId = ctx.PageId,
                        FromName = ctx.Title,
                        FromType = ctx.Type,
                        FromRealm = ctx.Realm,
                        ToId = targetPageId,
                        ToName = content,
                        ToType = "",
                        Label = edgeLabel,
                        Weight = weight,
                        Evidence = $"Infobox field '{label}'",
                        SourcePageId = ctx.PageId,
                        Continuity = ctx.Continuity,
                        FromYear = pl.FromYear,
                        ToYear = pl.ToYear,
                        Meta =
                            pl.Qualifier is null && pl.RawValue == content
                                ? null
                                : new EdgeMeta
                                {
                                    Qualifier = pl.Qualifier,
                                    RawValue = pl.RawValue != content ? pl.RawValue : null,
                                    Order = pl.Order,
                                },
                    }
                );
            }
        }
        else
        {
            // Fallback: no Values or no primary matches — emit edges for all links.
            foreach (var link in links)
            {
                var href = link.Contains(InfoboxBsonFields.Href) ? link[InfoboxBsonFields.Href].AsString : null;
                var content = link.Contains(InfoboxBsonFields.Content) ? link[InfoboxBsonFields.Content].AsString : null;
                if (href is null || content is null)
                    continue;

                var targetPageId = ResolveLinkTarget(href, content, ctx.WikiUrlToPageId);
                edges.Add(
                    new RelationshipEdge
                    {
                        FromId = ctx.PageId,
                        FromName = ctx.Title,
                        FromType = ctx.Type,
                        FromRealm = ctx.Realm,
                        ToId = targetPageId,
                        ToName = content,
                        ToType = "",
                        Label = edgeLabel,
                        Weight = weight * 0.8,
                        Evidence = $"Infobox field '{label}' (fallback)",
                        SourcePageId = ctx.PageId,
                        Continuity = ctx.Continuity,
                    }
                );
            }
        }

        if (links.Count == 0 && values.Count > 0)
            properties[label] = values;

        OnRelationshipExtracted(ctx, label, primaryLinks, properties);
    }

    // ── Link extraction ─────────────────────────────────────────────────

    /// <summary>
    /// One <c>Value</c> string after primary-link resolution. <see cref="Order"/>
    /// preserves the source <c>Values</c> array index so consumers (e.g.
    /// trade-route waypoint sequencing) can reconstruct ordering.
    /// </summary>
    protected internal readonly record struct PrimaryLink(BsonDocument Link, string? Qualifier, int? FromYear, int? ToYear, string RawValue, int Order);

    /// <summary>
    /// Resolve a link target to a PageId. Tries URL first, then link content text as title.
    /// Returns 0 if unresolved — the coordinator drops those edges in post-processing.
    /// </summary>
    protected static int ResolveLinkTarget(string? href, string? content, IReadOnlyDictionary<string, int> lookup)
    {
        if (href is not null && lookup.TryGetValue(href, out var byUrl))
            return byUrl;

        if (content is not null && lookup.TryGetValue(content, out var byContent))
            return byContent;

        if (href is not null && href.Contains("/wiki/"))
        {
            var titleFromUrl = Uri.UnescapeDataString(href[(href.LastIndexOf("/wiki/") + 6)..]).Replace('_', ' ');
            if (lookup.TryGetValue(titleFromUrl, out var byExtracted))
                return byExtracted;
        }

        return 0;
    }

    /// <summary>
    /// Match each Value string to its primary link from the link lookup.
    /// The primary entity is the text before the first '(' in the Value.
    /// Parenthetical text is treated as qualifier metadata, and temporal bounds are parsed from it.
    /// </summary>
    protected static List<PrimaryLink> ExtractPrimaryLinks(List<string> values, Dictionary<string, BsonDocument> linkLookup)
    {
        var results = new List<PrimaryLink>();
        if (values.Count == 0 || linkLookup.Count == 0)
            return results;

        for (var i = 0; i < values.Count; i++)
        {
            var value = values[i];
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var rawValue = value.Trim();

            var parenIdx = value.IndexOf('(');
            var primaryName = (parenIdx > 0 ? value[..parenIdx] : value).Trim().TrimEnd(',');

            string? qualifier = null;
            int? fromYear = null;
            int? toYear = null;

            if (parenIdx > 0)
            {
                var closeIdx = value.LastIndexOf(')');
                if (closeIdx > parenIdx)
                {
                    qualifier = value[(parenIdx + 1)..closeIdx].Trim();

                    var yearMatches = YearWithEra().Matches(qualifier);
                    if (yearMatches.Count >= 1)
                        fromYear = ParseGalacticYear(yearMatches[0].Value);
                    if (yearMatches.Count >= 2)
                        toYear = ParseGalacticYear(yearMatches[1].Value);
                }
            }

            if (linkLookup.TryGetValue(primaryName, out var matchedLink))
            {
                results.Add(new PrimaryLink(matchedLink, qualifier, fromYear, toYear, rawValue, i));
            }
            else if (linkLookup.TryGetValue(rawValue, out var fullMatch))
            {
                results.Add(new PrimaryLink(fullMatch, null, null, null, rawValue, i));
            }
            else
            {
                // Multi-entity fallback: many values contain multiple linked entities in
                // the same string (e.g. "Prime Minister Lama Su"). Iterate ALL links and
                // emit one per link whose content appears inside the primary name.
                // Downstream filters drop edges whose target type doesn't match the
                // relationship's expected target, so qualifier titles get pruned.
                var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (linkContent, linkDoc) in linkLookup)
                {
                    if (string.IsNullOrEmpty(linkContent))
                        continue;
                    if (primaryName.Contains(linkContent, StringComparison.OrdinalIgnoreCase) && emitted.Add(linkContent))
                    {
                        results.Add(new PrimaryLink(linkDoc, qualifier, fromYear, toYear, rawValue, i));
                    }
                }
            }
        }

        return results;
    }

    // ── Temporal parsing ────────────────────────────────────────────────

    /// <summary>
    /// Parse a date text like "22 BBY", "c. 5100 BBY", "19 BBY, Felucia" into a sort-key year.
    /// Returns negative for BBY, positive for ABY, null if unparseable.
    /// </summary>
    protected static int? ParseGalacticYear(string text)
    {
        var match = YearWithEra().Match(text);
        if (!match.Success)
            return null;
        if (!int.TryParse(match.Groups[1].Value.Replace(",", ""), out var num))
            return null;
        return match.Groups[2].Value.Equals("BBY", StringComparison.OrdinalIgnoreCase) ? -num : num;
    }

    /// <summary>
    /// Extract every BBY/ABY year occurrence from a range-style value like
    /// <c>"25,000 BBY – 19 BBY"</c>. Returns the parsed sort-key years sorted
    /// chronologically (most-negative first). Duplicates are deduped.
    /// </summary>
    protected static List<int> ParseAllGalacticYears(string text)
    {
        var matches = YearWithEra().Matches(text);
        var years = new SortedSet<int>();
        foreach (Match m in matches)
        {
            if (!int.TryParse(m.Groups[1].Value.Replace(",", ""), out var num))
                continue;
            var isBby = m.Groups[2].Value.Equals("BBY", StringComparison.OrdinalIgnoreCase);
            years.Add(isBby ? -num : num);
        }
        return [.. years];
    }

    /// <summary>
    /// Parse a real-world date text like "October 23, 1959", "September 2017", "2015" into a CE year.
    /// </summary>
    protected static int? ParseRealWorldYear(string text)
    {
        var match = RealWorldYear().Match(text);
        return match.Success && int.TryParse(match.Value, out var year) ? year : null;
    }

    /// <summary>
    /// Detect calendar system and parse year from date text.
    /// Tries galactic (BBY/ABY) first when hinted, then real-world, then unknown.
    /// </summary>
    protected static (string calendar, int? year) DetectCalendarAndParse(string text, string calendarHint)
    {
        if (calendarHint == "galactic")
        {
            var gy = ParseGalacticYear(text);
            return ("galactic", gy);
        }

        if (calendarHint == "real")
        {
            var ry = ParseRealWorldYear(text);
            return ("real", ry);
        }

        var galactic = ParseGalacticYear(text);
        if (galactic.HasValue)
            return ("galactic", galactic);

        var real = ParseRealWorldYear(text);
        if (real.HasValue)
            return ("real", real);

        return ("unknown", null);
    }

    // ── Facets ──────────────────────────────────────────────────────────

    /// <summary>
    /// Assign Order values to facets within each semantic dimension prefix.
    /// Groups by dimension (e.g. "institutional"), sorts by year (nulls last), assigns 0-based order.
    /// </summary>
    protected static void AssignFacetOrder(List<TemporalFacet> facets)
    {
        var groups = facets.GroupBy(f => f.Semantic.Split('.')[0]);
        foreach (var group in groups)
        {
            var ordered = group.OrderBy(f => f.Year.HasValue ? 0 : 1).ThenBy(f => f.Year ?? int.MaxValue).ToList();
            for (var i = 0; i < ordered.Count; i++)
                ordered[i].Order = i;
        }
    }

    // ── Label normalisation ─────────────────────────────────────────────

    /// <summary>
    /// Convert an infobox field label into a snake_case edge label.
    /// </summary>
    protected static string NormaliseLabel(string label)
    {
        var clean = label.Replace("(s)", "").Replace("(", "").Replace(")", "").Trim();

        var result = new StringBuilder();
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

    [GeneratedRegex(@"(\d[\d,]*)\s*(BBY|ABY)", RegexOptions.IgnoreCase)]
    private static partial Regex YearWithEra();

    [GeneratedRegex(@"\b(1[89]\d{2}|20[0-3]\d)\b")]
    private static partial Regex RealWorldYear();
}
