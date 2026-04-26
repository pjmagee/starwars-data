using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Location"/> extraction. Generic place node; pulls
/// region / sector / system membership via the standard relationship loop.
/// </summary>
public sealed class LocationNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Location;
}
