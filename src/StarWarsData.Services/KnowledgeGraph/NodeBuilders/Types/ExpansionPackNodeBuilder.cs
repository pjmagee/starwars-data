using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.ExpansionPack"/> extraction. Tabletop / TCG / digital
/// expansion releases. No type-specific logic yet.
/// </summary>
public sealed class ExpansionPackNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.ExpansionPack;
}
