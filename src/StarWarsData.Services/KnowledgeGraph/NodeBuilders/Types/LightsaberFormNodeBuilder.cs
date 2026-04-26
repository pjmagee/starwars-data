using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.LightsaberForm"/> extraction. Qualifier nodes for
/// the seven combat forms (Shii-Cho, Makashi, Soresu, etc.). Edges TO
/// LightsaberForm on person-relationship labels are dropped by the coordinator
/// post-processing.
/// </summary>
public sealed class LightsaberFormNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.LightsaberForm;
}
