using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Event"/> extraction. Catch-all for in-universe
/// events that don't fit a more specific event type. No type-specific
/// logic yet.
/// </summary>
public sealed class EventNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Event;
}
