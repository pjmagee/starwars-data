using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Unknown"/> handler. Used as the dispatch fallback
/// in <see cref="InfoboxGraphService"/> for any page whose template type
/// isn't a known <see cref="KgNodeTypes"/> constant — typically because the
/// template URL doesn't follow the expected <c>:Type</c> suffix or the
/// template is brand-new and not yet catalogued. Behaves identically to the
/// generic loop so the page still lands as a node.
/// </summary>
public sealed class UnknownNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Unknown;
}
