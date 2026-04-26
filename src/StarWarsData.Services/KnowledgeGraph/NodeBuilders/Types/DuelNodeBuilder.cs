using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Duel"/> extraction. Single combats — typically
/// Force-user vs Force-user. No type-specific logic yet.
/// </summary>
public sealed class DuelNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Duel;
}
