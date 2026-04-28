using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Substance"/> extraction. Materials, chemicals,
/// and compounds (kyber crystal, beskar, spice variants). No type-specific
/// logic yet — inherits the generic NodeBuilderBase loop.
/// </summary>
public sealed class SubstanceNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Substance;
}
