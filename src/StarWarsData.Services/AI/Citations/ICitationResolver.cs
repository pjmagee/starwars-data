using StarWarsData.Models.Queries;

namespace StarWarsData.Services.AI.Citations;

/// <summary>
/// Resolves a list of KG node ids into <see cref="CitationReference"/> records
/// — the agent's contract with the citation card UI. Single round trip per
/// resolve call regardless of count.
///
/// Phase 1 scope: direct spatial types get GalaxyMap links; every KG node
/// gets GraphExplorer + Wiki when those URLs exist. Indirect spatial linking
/// (Battle → location → galaxy map) is Phase 2; Timeline + Holocron surfaces
/// are Phase 4. See Design-030.
/// </summary>
public interface ICitationResolver
{
    Task<IReadOnlyList<CitationReference>> ResolveAsync(IReadOnlyList<int> pageIds, CancellationToken ct = default);
}
