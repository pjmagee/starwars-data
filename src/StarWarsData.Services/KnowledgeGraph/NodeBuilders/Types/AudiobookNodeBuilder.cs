using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Audiobook"/> extraction. No type-specific logic yet.
/// </summary>
public sealed class AudiobookNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Audiobook;
}
