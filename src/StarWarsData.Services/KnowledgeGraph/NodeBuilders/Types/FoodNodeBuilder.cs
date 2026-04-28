using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Food"/> extraction. No type-specific logic yet —
/// inherits the generic NodeBuilderBase loop. Phase C survey flagged
/// follow-up work for the <c>Race</c> field (carries Species links) and
/// <c>Inedible by</c> (mirror of <c>Edible by</c>); both currently handled
/// via FieldSemantics rather than per-type override.
/// </summary>
public sealed class FoodNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Food;
}
