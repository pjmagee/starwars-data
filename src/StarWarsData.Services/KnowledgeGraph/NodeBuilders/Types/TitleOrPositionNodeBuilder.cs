using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.TitleOrPosition"/> extraction. Qualifier nodes
/// (Jedi Master, Senator, etc.). Edges TO TitleOrPosition on person-relationship
/// labels are dropped by the coordinator post-processing — they're attributes
/// of the source entity, not separate edges.
///
/// <para>Design-024 Phase B: a TitleOrPosition's <c>Organization</c> (1156) and
/// <c>Government</c> (1069) infobox fields say "this position exists within this
/// (state-level) organization or government". Globally, <c>Government</c> maps to
/// <c>governed_by</c> (correct for CelestialBody/Law) and <c>Organization</c>
/// auto-normalises to <c>organization</c>. Neither carries the right semantics
/// here — so this <see cref="OnFinalize"/> override relabels them to
/// <c>position_in</c>. <c>Powers</c> and <c>Term length</c> are picked up as
/// scalar properties via direct <see cref="Definitions.FieldSemantics.Properties"/>
/// entries.</para>
/// </summary>
public sealed class TitleOrPositionNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.TitleOrPosition;

    static readonly HashSet<string> PositionInFields = new(StringComparer.OrdinalIgnoreCase) { "Organization", "Government" };

    protected override void OnFinalize(NodeBuilderContext ctx, GraphNode node, List<RelationshipEdge> edges)
    {
        foreach (var edge in edges)
        {
            var sourceField = edge.Meta?.SourceFieldLabel;
            if (sourceField is null || !PositionInFields.Contains(sourceField))
                continue;

            var targetType = ctx.NodeTypeByPageId.GetValueOrDefault(edge.ToId, "");
            if (targetType is KgNodeTypes.Organization or KgNodeTypes.Government)
                edge.Label = "position_in";
        }
    }
}
