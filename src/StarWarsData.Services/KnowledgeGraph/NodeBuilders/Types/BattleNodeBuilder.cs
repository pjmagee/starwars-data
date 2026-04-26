using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Battle"/> extraction.
/// Per <c>eng/design/007-kg-per-type-builders.md</c>, future enhancement:
/// belligerent / commander side-grouping (attacker vs defender) preserved
/// on edge metadata. Currently the side information is implicit in the
/// infobox section (Belligerents 1 / Belligerents 2) and lost during
/// generic edge extraction.
/// </summary>
public sealed class BattleNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Battle;
}
