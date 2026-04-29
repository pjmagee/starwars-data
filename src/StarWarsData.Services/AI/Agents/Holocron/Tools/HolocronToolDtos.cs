namespace StarWarsData.Services.AI.Agents.Holocron.Tools;

// Return-shape DTOs for the Holocron v2 read toolkit (Design-025 §The tool surface).
// Records (not classes) so AIFunctionFactory's structured-output binder produces
// stable, deterministic JSON for the model. Each tool returns either a value or
// null — null is a meaningful signal the agent must reason about (e.g. "this string
// is not a node, so encode the fact as a property" or "no canonical label covers
// this target type, so emit suggest_new_label instead of guessing").

/// <summary>
/// Result of <c>resolve_entity</c>. Null return means the input string is NOT a
/// node in <c>kg.nodes</c>; the agent should treat the string as a property value
/// rather than an edge target.
/// </summary>
public sealed record ResolveEntityResult(int PageId, string Name, string Type, string? WikiUrl);

/// <summary>
/// One canonical edge label valid for a given <c>(sourceType, targetType)</c>
/// pair. Returned by <c>find_canonical_label</c> as part of a list so the agent
/// can read each label's description and pick the one matching the prose. Per
/// Design-024 the per-source-type relabel rules are baked into
/// <see cref="ExpectedTargetTypes"/> already (e.g. <c>has_role</c> targets
/// <c>TitleOrPosition</c>, so a Character→TitleOrPosition query surfaces
/// <c>has_role</c> rather than <c>affiliated_with</c>), so target-side
/// filtering alone yields the right slice without a synonym table.
/// </summary>
public sealed record CanonicalLabelOption(string Label, string ReverseLabel, string Description, IReadOnlyList<string> ExpectedTargetTypes, string Category);

/// <summary>
/// One edge already on the graph between the queried <c>(fromId, toId)</c> pair,
/// in either direction. <see cref="Direction"/> is "forward" when the queried
/// from→to matches the stored fromId→toId, "reverse" when it's swapped.
/// <see cref="Source"/> is "kg.edges" for Phase 1 wiki edges, or "enrichment"
/// for active Phase 2 Holocron edges (the agent should treat both as existing
/// connections and use Annotate / FillGap instead of Add).
/// </summary>
public sealed record ExistingEdgeRow(int FromId, int ToId, string Label, string Direction, int? FromYear, int? ToYear, string Source);

/// <summary>
/// Per-template schema returned by <c>get_template_schema</c>. The agent calls
/// this once at the start of a batch to learn what fieldPaths are valid for the
/// target node's template (so it can avoid <c>Primary role(s)</c> on a Character)
/// and which infobox fields produce edges (so it can model relationships
/// already encoded in the wiki without duplicating them).
/// </summary>
public sealed record TemplateSchemaInfo(string Template, IReadOnlyList<string> Properties, IReadOnlyList<RelationshipFieldInfo> Relationships, IReadOnlyList<TemporalFieldInfo> TemporalFields);

/// <summary>One relationship-typed infobox field for a given template.</summary>
public sealed record RelationshipFieldInfo(string Field, string Label, string ReverseLabel, IReadOnlyList<string> ExpectedTargetTypes, string Description);

/// <summary>One temporal-typed infobox field for a given template.</summary>
public sealed record TemporalFieldInfo(string Field, string Semantic, string Calendar, bool IsRange);
