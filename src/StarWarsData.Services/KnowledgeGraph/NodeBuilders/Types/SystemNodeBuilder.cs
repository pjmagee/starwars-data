using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.System"/> extraction.
/// Per <c>eng/design/035-kg-per-type-builders.md</c>, future enhancement:
/// preserve the orbital hierarchy distinction (planets vs moons vs asteroids
/// vs space stations) currently flattened into <c>orbited_by</c> edges.
/// </summary>
public sealed class SystemNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.System;
}
