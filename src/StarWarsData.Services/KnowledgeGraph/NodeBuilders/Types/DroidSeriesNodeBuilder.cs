using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.DroidSeries"/> extraction. Production families of
/// droids (e.g. B1 battle droid series, R-series astromechs). Distinct from
/// individual <see cref="KgNodeTypes.Droid"/> instances. No type-specific
/// logic yet — inherits the generic NodeBuilderBase loop.
/// </summary>
public sealed class DroidSeriesNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.DroidSeries;
}
