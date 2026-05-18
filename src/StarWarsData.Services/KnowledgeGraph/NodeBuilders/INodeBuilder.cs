namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders;

/// <summary>
/// Builds a single <see cref="StarWarsData.Models.Entities.GraphNode"/> (and its outbound
/// <see cref="StarWarsData.Models.Entities.RelationshipEdge"/>s) for one infobox-bearing page.
///
/// One implementation handles each KG node type that needs type-specific extraction
/// behaviour. <see cref="DefaultNodeBuilder"/> covers everything that doesn't.
///
/// See <c>eng/design/035-kg-per-type-builders.md</c> for the rationale.
/// </summary>
public interface INodeBuilder
{
    /// <summary>
    /// The KG node type this builder handles. Used as the dispatch key in
    /// <see cref="InfoboxGraphService"/>'s registry.
    /// </summary>
    string NodeType { get; }

    /// <summary>
    /// Extract the node and its edges from the provided page context.
    /// Pure: no I/O, no MongoDB calls, no LLM. Edges may carry unresolved
    /// targets (<c>ToId == 0</c>) — the coordinator filters those downstream.
    /// </summary>
    NodeBuilderResult Build(NodeBuilderContext context);
}
