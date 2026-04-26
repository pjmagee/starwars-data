using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.TitleOrPosition"/> extraction. Qualifier nodes
/// (Jedi Master, Senator, etc.). Edges TO TitleOrPosition on person-relationship
/// labels are dropped by the coordinator post-processing — they're attributes
/// of the source entity, not separate edges.
/// </summary>
public sealed class TitleOrPositionNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.TitleOrPosition;
}
