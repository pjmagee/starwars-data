namespace StarWarsData.Services;

// Concrete DTOs returned from AI toolkit tool methods.
//
// Tool methods must NOT return JSON strings — AIFunctionFactory (Microsoft.Extensions.AI)
// auto-serializes concrete return types using web-style (camelCase) options, and exposes a
// JSON schema for each tool's return value so the model sees the field names explicitly.
// Returning pre-serialized strings breaks schema inference and makes missing/typo'd fields
// silent.
//
// Property names are PascalCase here and serialize to camelCase at the boundary.

public sealed record KgTemporalFacetDto(string Field, string Semantic, string Calendar, int? Year, string Text, int? Order = null);

public sealed record KgNodeDto(int PageId, string Name, string Type, string Continuity, string? ImageUrl, string? WikiUrl, int? StartYear, int? EndYear, List<KgTemporalFacetDto> TemporalFacets);

public sealed record KgNodeDetailDto(
    int? PageId,
    string? Name,
    string? Type,
    string? Continuity,
    Dictionary<string, List<string>>? Properties,
    int? StartYear,
    int? EndYear,
    List<KgTemporalFacetDto>? TemporalFacets,
    string? ImageUrl,
    string? WikiUrl,
    string? Error = null
);

public sealed record EntityTimelineDto(
    int? PageId,
    string? Name,
    string? Type,
    string? Continuity,
    string? Realm,
    int? StartYear,
    int? EndYear,
    string? Duration,
    string? WikiUrl,
    List<KgTemporalFacetDto>? TemporalFacets,
    string? Error = null
);

public sealed record RelationshipTargetDto(int PageId, string Name, string Type, double Weight, string Evidence, Dictionary<string, string>? Properties = null);

public sealed record EntityRelationshipsDto(int EntityId, string? EntityName, Dictionary<string, List<RelationshipTargetDto>> Relationships, int TotalEdges, string? Note = null);

public sealed record CategoryRelationshipTargetDto(int PageId, string Name, string Type, int? FromYear, int? ToYear);

public sealed record RelationshipsByCategoryDto(
    int EntityId,
    string? EntityName,
    string Category,
    Dictionary<string, List<CategoryRelationshipTargetDto>> Relationships,
    int TotalEdges,
    string? Note = null
);

public sealed record RelationshipTypeDto(string Label, int Count, double AvgWeight);

public sealed record GraphNodeDto(int PageId, string Name, string Type);

public sealed record GraphEdgeDto(int From, int To, string Label, double Weight);

public sealed record GraphTraversalRootDto(int Id);

public sealed record GraphTraversalDto(GraphTraversalRootDto Root, List<GraphNodeDto> Nodes, List<GraphEdgeDto> Edges, string Summary);

public sealed record ConnectionStepDto(int FromId, string From, string FromType, int ToId, string To, string ToType, string Label, string Evidence);

public sealed record ConnectionsDto(bool Connected, int? PathLength = null, List<ConnectionStepDto>? Path = null, int? SearchedHops = null, string? Note = null);

/// <summary>Input record for render_path tool — maps from find_connections() output.</summary>
public sealed record PathStepInput(int FromId, string FromName, string FromType, int ToId, string ToName, string ToType, string Label, string Evidence);

public sealed record LineageStepDto(int Hop, int FromId, string FromName, string FromType, int ToId, string ToName, string ToType, string Label);

public sealed record LineageDto(int RootId, string RootName, string Label, string Direction, int Depth, List<LineageStepDto> Chain, string? Note = null);

public sealed record SemanticSearchHitDto(int PageId, string Title, string? Heading, string? Type, string? Continuity, double Score, string? WikiUrl, string? SectionUrl, string Text);

public sealed record SemanticSearchResultDto(string Query, List<SemanticSearchHitDto> Results, int TotalResults, string? Note = null);

public sealed record GalaxyFactionDto(string Faction, string Control, bool Contested);

public sealed record GalaxyRegionDto(string Region, List<GalaxyFactionDto> Factions);

public sealed record GalaxyEventDto(string Title, string? Lens, string? Place, string? Outcome, string? WikiUrl, string Continuity);

public sealed record GalaxyYearResultDto(
    int? Year,
    string? YearDisplay,
    string? Era,
    string? EraDescription,
    List<GalaxyRegionDto>? TerritoryControl,
    List<GalaxyEventDto>? EventsOnMap,
    int? TotalEvents,
    string? Error = null,
    List<int>? NearestYears = null
);

public sealed record TypeCountDto(string Type, int Count);

public sealed record LabelCountDto(string Label, int Count);

public sealed record NamedCountDto(string Name, int PageId, int Count);

public sealed record ValueCountDto(string Value, int Count);

public sealed record YearCountDto(int Year, string YearDisplay, int Count);

public sealed record NamedDegreeDto(string Name, int PageId, int Degree);

public sealed record DimensionCountDto(string Dimension, int Count);

public sealed record EntityComparisonDto(int EntityId, string Name, List<DimensionCountDto> Dimensions);

public sealed record RelationshipLabelDto(string Label, string? Reverse, string? Description, List<string>? FromTypes, List<string>? ToTypes, int UsageCount);

public sealed record TemporalFieldDto(string Field, string Semantic, string Calendar);

public sealed record SchemaRelationshipDto(string Field, string Label, string? Reverse, List<string>? Targets, string? Category, string? Description, bool Primary);

public sealed record EntitySchemaDto(string Type, List<string> Properties, List<TemporalFieldDto> TemporalFields, List<SchemaRelationshipDto> Relationships);

public sealed record LifecycleTransitionDetailDto(string Semantic, int? Year, string Text, string Calendar);

public sealed record LifecycleTransitionDto(int PageId, string Name, string Type, LifecycleTransitionDetailDto Transition);

public sealed record CategoryLabelDto(string Label, string? Reverse, string? Description, List<string>? Targets);

public sealed record LabelsByCategoryDto(string Category, int Count, List<CategoryLabelDto> Labels);

// ===== DataExplorerToolkit =====

public sealed record PageSummaryDto(int Id, string Name, string Continuity, string WikiUrl);

public sealed record InfoboxDataRowDto(string Label, List<string> Values);

public sealed record PageDetailDto(int Id, string Continuity, string WikiUrl, List<InfoboxDataRowDto> Data);

public sealed record PageMatchDto(int Id, string Name, List<string>? MatchValue, string Continuity, string WikiUrl);

public sealed record PageDateMatchDto(int Id, string Name, List<string>? Date, string Continuity, string WikiUrl);

public sealed record LabelPageCountDto(string Label, int PageCount);

public sealed record LinkLabelDetailDto(string Label, int LinkCount, List<string> SampleLinks);

// ===== RelationshipAnalystToolkit =====

public sealed record InfoboxLinkDto(string Text, string Href);

public sealed record InfoboxRowWithLinksDto(string Label, List<string> Values, List<InfoboxLinkDto> Links);

public sealed record PageContentDto(
    int? PageId,
    string? Title,
    string? Type,
    string? Continuity,
    string? ImageUrl,
    string? WikiUrl,
    List<InfoboxRowWithLinksDto>? Infobox,
    string? Content,
    string? Error = null
);

public sealed record LinkedPageDto(int PageId, string Name, string Type, string Continuity);

public sealed record SimilarLabelDto(string Label, string? Reverse, string? Description, int UsageCount, double Score);

public sealed record StoredEdgeDto(int ToId, string ToName, string ToType, string Label, double Weight, string Evidence);

public sealed record StoreEdgesResultDto(int Inserted, int Total);

public sealed record StatusDto(string Status, int PageId, string? Reason = null);
