using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Artifact"/> extraction. Force / Sith / Jedi relics
/// (holocrons, ancient weapons). No type-specific logic yet.
/// </summary>
public sealed class ArtifactNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Artifact;
}
