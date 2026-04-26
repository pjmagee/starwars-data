using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Device"/> extraction. Technological items that
/// don't fit weapon / vehicle / droid (comlinks, holocrons-as-tech, etc.).
/// No type-specific logic yet.
/// </summary>
public sealed class DeviceNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Device;
}
