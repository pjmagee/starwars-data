using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Company"/> extraction. In-universe corporations
/// (manufacturers, trade houses). Real-world publishing companies use
/// <see cref="KgNodeTypes.RealCompany"/> instead. No type-specific logic yet.
/// </summary>
public sealed class CompanyNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Company;
}
