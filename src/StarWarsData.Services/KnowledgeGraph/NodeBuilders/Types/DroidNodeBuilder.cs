using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Droid"/> extraction. Specific droids (R2-D2, C-3PO).
/// No type-specific logic yet.
/// </summary>
public sealed class DroidNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Droid;
}
