using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Year"/> extraction. Pure temporal markers; edges TO
/// Year nodes are dropped by the coordinator post-processing.
///
/// <para>Design-024 Phase B: <c>Chancellor</c> (134), <c>Chief</c> (43),
/// <c>Head</c> (25) are typed-leader fields on Year pages — promoted from
/// silent unclassified text to typed relationships
/// (<c>has_chancellor</c>, <c>has_chief</c>, <c>has_head</c>) via direct
/// <see cref="Definitions.FieldSemantics.Relationships"/> entries. The generic
/// loop picks them up automatically — no <c>OnFinalize</c> override required.</para>
/// </summary>
public sealed class YearNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Year;
}
