using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.City"/> extraction. Sub-planetary settlement; relies
/// on the generic location-hierarchy edge resolution.
/// </summary>
public sealed class CityNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.City;
}
