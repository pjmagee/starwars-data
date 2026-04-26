using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders;

/// <summary>
/// Output of an <see cref="INodeBuilder"/>. Edges may carry unresolved targets
/// (<c>ToId == 0</c>); the coordinator drops those during post-processing.
/// </summary>
public sealed record NodeBuilderResult(GraphNode Node, IReadOnlyList<RelationshipEdge> Edges);
