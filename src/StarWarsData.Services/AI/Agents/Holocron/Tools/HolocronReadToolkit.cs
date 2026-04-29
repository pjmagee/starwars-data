using System.ComponentModel;
using Microsoft.Extensions.AI;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Services.KnowledgeGraph.Definitions;

namespace StarWarsData.Services.AI.Agents.Holocron.Tools;

/// <summary>
/// Holocron v2 read-tool surface (Design-025 §The tool surface, Design-026 §Phase A.1).
/// Four deterministic, side-effect-free lookups:
///
/// <list type="bullet">
///   <item><c>resolve_entity</c> — string → kg.nodes match, the gate that decides
///         "is this a graph node or a property value?".</item>
///   <item><c>find_canonical_label</c> — list of canonical edge labels valid for a
///         given (sourceType, targetType) pair. The agent reads each label's
///         description and picks the one that matches the prose.</item>
///   <item><c>check_existing_edges</c> — what edges already exist between a pair, in either
///         direction, including active Holocron enrichments.</item>
///   <item><c>get_template_schema</c> — what properties / relationships / temporal fields
///         are valid for the target node's template.</item>
/// </list>
///
/// <para>
/// Bound to the agent at construction via <see cref="AsAIFunctions"/>. The Phase A.3
/// write tools (<c>propose_edge</c>, <c>propose_property</c>, <c>suggest_new_label</c>)
/// will live in a sibling toolkit because they carry per-batch state.
/// </para>
/// </summary>
public sealed class HolocronReadToolkit
{
    readonly IMongoCollection<GraphNode> _nodes;
    readonly IMongoCollection<RelationshipEdge> _edges;
    readonly IMongoCollection<EdgeEnrichment> _edgeEnrichments;

    public HolocronReadToolkit(IMongoClient mongoClient, string databaseName)
    {
        var db = mongoClient.GetDatabase(databaseName);
        _nodes = db.GetCollection<GraphNode>(Collections.KgNodes);
        _edges = db.GetCollection<RelationshipEdge>(Collections.KgEdges);
        _edgeEnrichments = db.GetCollection<EdgeEnrichment>(Collections.KgEdgeEnrichments);
    }

    [Description(
        """
            Reso
            lve a free-text entity mention (e.g. "bounty hunter", "Count Dooku") to a
            knowledge-graph node. Returns the node's PageId, canonical name, and entity
            type when the string matches an existing node in kg.nodes; returns null when
            no node matches.

            A null return is meaningful: it tells the caller the string is NOT a graph
            node and so should be encoded as a property value (via propose_property),
            not as an edge target. Conversely, a successful resolve means the caller
            SHOULD prefer an edge encoding (via propose_edge with find_canonical_label)
            over a property — that's the rule that kills the "Aliases stuffed with
            roles" failure mode (Design-025 §Failure-mode mapping).

            Matching is case-insensitive on the canonical node name. Alias matching is
            not yet implemented in Phase A.1.
            """
    )]
    public async Task<ResolveEntityResult?> ResolveEntity([Description("Free-text entity mention from a chunk (e.g. 'bounty hunter', 'Count Dooku')")] string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim();
        var filter = Builders<GraphNode>.Filter.Regex(n => n.Name, new MongoDB.Bson.BsonRegularExpression($"^{System.Text.RegularExpressions.Regex.Escape(trimmed)}$", "i"));
        var node = await _nodes
            .Find(filter)
            .Project(n => new GraphNode
            {
                PageId = n.PageId,
                Name = n.Name,
                Type = n.Type,
                WikiUrl = n.WikiUrl,
            })
            .FirstOrDefaultAsync();
        return node is null ? null : new ResolveEntityResult(node.PageId, node.Name, node.Type, node.WikiUrl);
    }

    [Description(
        """
            List the canonical edge labels valid for a (sourceType, targetType) pair.
            The agent reads each label's description and picks the one matching the
            prose — no synonym translation, no guessing. When no canonical label
            accepts the requested target type, returns an empty list and the caller
            should emit suggest_new_label rather than forcing a typed mismatch.

            Per-source-type relabel rules (Design-024) are baked into each label's
            ExpectedTargetTypes, so a Character→TitleOrPosition query naturally
            surfaces has_role rather than affiliated_with. Source-type is advisory
            in Phase A.1; v1 filtering is purely target-side.

            Permissive labels (e.g. followed_by, preceded_by) declare no target type
            and are always included — they're valid for any target.
            """
    )]
    public IReadOnlyList<CanonicalLabelOption> FindCanonicalLabel(
        [Description("Source-node entity type (e.g. 'Character', 'Organization'). Advisory only in v1.")] string sourceType,
        [Description("Target-node entity type (e.g. 'TitleOrPosition', 'Battle', 'Family'). Drives the filter.")] string targetType
    )
    {
        if (string.IsNullOrWhiteSpace(targetType))
            return [];

        return FieldSemantics
            .Relationships.Values.DistinctBy(d => d.Label, StringComparer.OrdinalIgnoreCase)
            .Where(d => d.ExpectedTargetTypes.Length == 0 || d.ExpectedTargetTypes.Contains(targetType, StringComparer.OrdinalIgnoreCase))
            .Select(d => new CanonicalLabelOption(d.Label, d.Reverse, d.Description, d.ExpectedTargetTypes, d.Category))
            .OrderBy(o => o.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    [Description(
        """
            List every edge already on the graph between the (fromId, toId) pair, in
            either direction, including active Holocron enrichments. The agent uses
            this to decide between Annotate (existing edge — refine context),
            FillGap (existing edge with null bounds — supply years), and Add (no
            existing connection — propose a new edge).

            Returns an empty list when the pair has no recorded relationship in
            either direction. Phase 1 wiki edges have Source = "kg.edges"; active
            Phase 2 enrichments have Source = "enrichment".
            """
    )]
    public async Task<IReadOnlyList<ExistingEdgeRow>> CheckExistingEdges([Description("First node PageId")] int fromId, [Description("Second node PageId")] int toId)
    {
        if (fromId <= 0 || toId <= 0 || fromId == toId)
            return [];

        var rows = new List<ExistingEdgeRow>();

        var pairFilter = Builders<RelationshipEdge>.Filter.Or(
            Builders<RelationshipEdge>.Filter.Eq(e => e.FromId, fromId) & Builders<RelationshipEdge>.Filter.Eq(e => e.ToId, toId),
            Builders<RelationshipEdge>.Filter.Eq(e => e.FromId, toId) & Builders<RelationshipEdge>.Filter.Eq(e => e.ToId, fromId)
        );

        var phase1 = await _edges.Find(pairFilter).ToListAsync();
        foreach (var e in phase1)
        {
            var direction = e.FromId == fromId ? "forward" : "reverse";
            rows.Add(new ExistingEdgeRow(e.FromId, e.ToId, e.Label, direction, e.FromYear, e.ToYear, "kg.edges"));
        }

        var enrichmentPairFilter =
            Builders<EdgeEnrichment>.Filter.Eq(e => e.Status, EnrichmentStatus.Active)
            & Builders<EdgeEnrichment>.Filter.Or(
                Builders<EdgeEnrichment>.Filter.Eq(e => e.FromId, fromId) & Builders<EdgeEnrichment>.Filter.Eq(e => e.ToId, toId),
                Builders<EdgeEnrichment>.Filter.Eq(e => e.FromId, toId) & Builders<EdgeEnrichment>.Filter.Eq(e => e.ToId, fromId)
            );

        var phase2 = await _edgeEnrichments.Find(enrichmentPairFilter).ToListAsync();
        foreach (var e in phase2)
        {
            var direction = e.FromId == fromId ? "forward" : "reverse";
            rows.Add(new ExistingEdgeRow(e.FromId, e.ToId, e.Label, direction, ReadYear(e.Value, "fromYear"), ReadYear(e.Value, "toYear"), "enrichment"));
        }

        return rows;
    }

    static int? ReadYear(MongoDB.Bson.BsonDocument value, string key) => value.TryGetValue(key, out var v) && v.IsInt32 ? v.AsInt32 : null;

    [Description(
        """
            Return the schema for a node template (e.g. 'Character', 'Battle',
            'Starship'): the property fieldPaths, relationship-typed infobox fields
            (each with its canonical label and expected target types), and temporal
            fields. The agent calls this once at the start of a batch so it knows
            which fieldPaths are valid for propose_property and which relationships
            the wiki already encodes for this template.

            Unknown template names return the registry's permissive fallback
            definition, which exposes the full global semantic dictionary.
            """
    )]
    public TemplateSchemaInfo GetTemplateSchema([Description("Node-template name (e.g. 'Character', 'Battle', 'Starship'). Case-insensitive.")] string nodeType)
    {
        var def = InfoboxDefinitionRegistry.ForTemplate(nodeType);

        var properties = def.Properties.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

        var relationships = def
            .Relationships.Select(kvp => new RelationshipFieldInfo(
                Field: kvp.Key,
                Label: kvp.Value.Label,
                ReverseLabel: kvp.Value.Reverse,
                ExpectedTargetTypes: kvp.Value.ExpectedTargetTypes,
                Description: kvp.Value.Description
            ))
            .OrderBy(r => r.Field, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var temporal = def
            .TemporalFields.Select(kvp => new TemporalFieldInfo(Field: kvp.Key, Semantic: kvp.Value.Semantic, Calendar: kvp.Value.Calendar, IsRange: kvp.Value.IsRange))
            .OrderBy(t => t.Field, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new TemplateSchemaInfo(def.TypeName, properties, relationships, temporal);
    }

    /// <summary>
    /// Bind every read tool as an <see cref="AITool"/> for attachment to an
    /// <c>AIAgent</c>. Names use <see cref="ToolNames.Holocron"/>.
    /// </summary>
    public IReadOnlyList<AITool> AsAIFunctions() =>
        [
            AIFunctionFactory.Create(ResolveEntity, ToolNames.Holocron.ResolveEntity),
            AIFunctionFactory.Create(FindCanonicalLabel, ToolNames.Holocron.FindCanonicalLabel),
            AIFunctionFactory.Create(CheckExistingEdges, ToolNames.Holocron.CheckExistingEdges),
            AIFunctionFactory.Create(GetTemplateSchema, ToolNames.Holocron.GetTemplateSchema),
        ];
}
