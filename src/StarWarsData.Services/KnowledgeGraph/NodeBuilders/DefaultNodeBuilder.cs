using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders;

/// <summary>
/// Parameterized <see cref="INodeBuilder"/> for KG node types that need no
/// type-specific extraction logic beyond an optional <see cref="OnFinalize"/>
/// hook. Collapses the former one-property stub builders and single-delegation
/// OnFinalize wrappers into one class (~12 lines instead of ~800).
///
/// <para>Use the parameterless-hook form for pure stubs:
/// <c>new DefaultNodeBuilder(KgNodeTypes.Person)</c>. Pass an
/// <c>onFinalize</c> delegate for single-helper types
/// (ISBN normalisation, conflict side encoding).</para>
/// </summary>
public sealed class DefaultNodeBuilder : NodeBuilderBase
{
    readonly Action<NodeBuilderContext, GraphNode, List<RelationshipEdge>>? _onFinalize;

    public DefaultNodeBuilder(string nodeType, Action<NodeBuilderContext, GraphNode, List<RelationshipEdge>>? onFinalize = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeType);
        NodeType = nodeType;
        _onFinalize = onFinalize;
    }

    public override string NodeType { get; }

    protected override void OnFinalize(NodeBuilderContext ctx, GraphNode node, List<RelationshipEdge> edges) => _onFinalize?.Invoke(ctx, node, edges);
}
