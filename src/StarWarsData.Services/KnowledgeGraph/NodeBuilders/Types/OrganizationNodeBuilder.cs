using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Organization"/> extraction. Non-government groups
/// (corporations, criminal syndicates, religious orders). No type-specific
/// logic yet.
/// </summary>
public sealed class OrganizationNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Organization;
}
