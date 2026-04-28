using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.MilitaryUnit"/> extraction. Battalions, legions,
/// divisions, fleet sub-units. The type-name string is <c>"Military_unit"</c>
/// (matches Wookieepedia's template suffix); the C# constant is
/// <c>MilitaryUnit</c> for PascalCase identifier convention.
///
/// No type-specific logic yet — inherits the generic NodeBuilderBase loop.
/// Replaces the broken <c>MilitaryNodeBuilder</c> whose <c>NodeType</c>
/// returned <c>"Military"</c> and never matched any corpus row.
/// </summary>
public sealed class MilitaryUnitNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.MilitaryUnit;
}
