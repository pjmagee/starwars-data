using MongoDB.Bson;
using StarWarsData.Models.Entities;
using StarWarsData.Services.KnowledgeGraph.Definitions;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders;

/// <summary>
/// Per-page input passed to <see cref="INodeBuilder.Build"/>. Pure data —
/// no MongoDB collections, no I/O.
/// </summary>
/// <param name="PageId">Wookieepedia PageId; matches <c>_id</c> in <c>raw.pages</c>.</param>
/// <param name="Title">Page title used as the node's display name.</param>
/// <param name="Type">Resolved KG node type (e.g. <c>Character</c>, <c>TradeRoute</c>).</param>
/// <param name="Continuity">Canon / Legends classification.</param>
/// <param name="Realm">Star Wars / Real-world classification.</param>
/// <param name="ContentHash">Hash of the source page content at extraction time.</param>
/// <param name="WikiUrl">Canonical wiki URL of the source page.</param>
/// <param name="ImageUrl">Optional infobox image URL.</param>
/// <param name="DataItems">Raw infobox <c>data</c> array — each entry is a <c>{label, values, links}</c> document.</param>
/// <param name="Definition">Template-scoped semantic definition (which labels are properties / relationships / temporal).</param>
/// <param name="WikiUrlToPageId">Lookup from wiki URL or title to PageId, used to resolve link targets.</param>
public sealed record NodeBuilderContext(
    int PageId,
    string Title,
    string Type,
    Continuity Continuity,
    Realm Realm,
    string? ContentHash,
    string? WikiUrl,
    string? ImageUrl,
    BsonArray DataItems,
    InfoboxDefinition Definition,
    IReadOnlyDictionary<string, int> WikiUrlToPageId
);
