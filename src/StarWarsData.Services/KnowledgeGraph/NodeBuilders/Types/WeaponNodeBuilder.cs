using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Weapon"/> extraction. Generic weapons (blasters,
/// projectile weapons, melee). No type-specific logic yet.
/// </summary>
public sealed class WeaponNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Weapon;
}
