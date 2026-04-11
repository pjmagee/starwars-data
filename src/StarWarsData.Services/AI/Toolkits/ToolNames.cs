namespace StarWarsData.Services;

/// <summary>
/// Canonical tool names registered by the AI toolkits.
///
/// Single source of truth — every <c>AIFunctionFactory.Create(..., name)</c> call in
/// the Services project and every test assertion that references a tool name should
/// use one of these constants instead of a string literal. Renaming a tool then
/// becomes a single edit picked up by the compiler everywhere.
///
/// Grouped by toolkit so the calling site stays self-documenting (e.g.
/// <c>ToolNames.GraphRAG.SearchEntities</c>).
/// </summary>
public static class ToolNames
{
    /// <summary><see cref="ComponentToolkit"/> — UI render descriptors.</summary>
    public static class Component
    {
        public const string RenderTable = "render_table";
        public const string RenderDataTable = "render_data_table";
        public const string RenderChart = "render_chart";
        public const string RenderGraph = "render_graph";
        public const string RenderPath = "render_path";
        public const string RenderTimeline = "render_timeline";
        public const string RenderInfobox = "render_infobox";
        public const string RenderMarkdown = "render_markdown";
        public const string RenderAurebesh = "render_aurebesh";
    }

    /// <summary><see cref="GraphRAGToolkit"/> — KG-backed retrieval and semantic search.</summary>
    public static class GraphRAG
    {
        public const string SearchEntities = "search_entities";
        public const string FindEntitiesByYear = "find_entities_by_year";
        public const string GetEntityProperties = "get_entity_properties";
        public const string GetEntityTimeline = "get_entity_timeline";
        public const string GetEntityRelationships = "get_entity_relationships";
        public const string GetRelationshipTypes = "get_relationship_types";
        public const string TraverseGraph = "traverse_graph";
        public const string GetLineage = "get_lineage";
        public const string FindConnections = "find_connections";
        public const string GetGalaxyYear = "get_galaxy_year";
        public const string ListEntityTypes = "list_entity_types";
        public const string ListRelationshipLabels = "list_relationship_labels";
        public const string SemanticSearch = "semantic_search";
    }

    /// <summary><see cref="DataExplorerToolkit"/> — raw <c>raw.pages</c> lookups.</summary>
    public static class DataExplorer
    {
        public const string SearchPagesByName = "search_pages_by_name";
        public const string GetPageById = "get_page_by_id";
        public const string GetPageProperty = "get_page_property";
        public const string SearchPagesByProperty = "search_pages_by_property";
        public const string SearchPagesByDate = "search_pages_by_date";
        public const string SearchPagesByLink = "search_pages_by_link";
        public const string SamplePropertyValues = "sample_property_values";
        public const string SampleLinkLabels = "sample_link_labels";
        public const string ListInfoboxTypes = "list_infobox_types";
        public const string ListInfoboxLabels = "list_infobox_labels";
        public const string ListTimelineCategories = "list_timeline_categories";
    }

    /// <summary><see cref="KGAnalyticsToolkit"/> — aggregation and counting over the KG.</summary>
    public static class KGAnalytics
    {
        public const string DescribeRelationshipLabels = "describe_relationship_labels";
        public const string CountRelatedEntities = "count_related_entities";
        public const string CountNodesByProperty = "count_nodes_by_property";
        public const string CountByYearRange = "count_by_year_range";
        public const string CountEdgesBetweenTypes = "count_edges_between_types";
        public const string TopConnectedEntities = "top_connected_entities";
        public const string EntityProfile = "entity_profile";
        public const string CountNodesByType = "count_nodes_by_type";
        public const string CountPropertyForRelatedEntities = "count_property_for_related_entities";
        public const string GroupEntitiesByConnection = "group_entities_by_connection";
        public const string CompareEntities = "compare_entities";
        public const string DescribeEntitySchema = "describe_entity_schema";
        public const string FindByLifecycleTransition = "find_by_lifecycle_transition";
        public const string CountLifecycleTransitions = "count_lifecycle_transitions";
        public const string ListLabelsByCategory = "list_labels_by_category";
        public const string GetRelationshipsByCategory = "get_relationships_by_category";
    }

    /// <summary>
    /// <see cref="RelationshipAnalystToolkit"/> — internal tools for the offline
    /// relationship-graph batch builder. Not exposed to the user-facing Ask AI agent.
    /// </summary>
    public static class RelationshipAnalyst
    {
        public const string GetPageContent = "get_page_content";
        public const string GetLinkedPages = "get_linked_pages";
        public const string GetExistingLabels = "get_existing_labels";
        public const string FindSimilarLabel = "find_similar_label";
        public const string GetEntityEdges = "get_entity_edges";
        public const string StoreEdges = "store_edges";
        public const string MarkProcessed = "mark_processed";
        public const string SkipPage = "skip_page";
    }

    /// <summary>Wiki / external search tools registered outside the toolkit classes.</summary>
    public static class Wiki
    {
        public const string KeywordSearch = "keyword_search";
    }
}
