using StarWarsData.Models.Entities;

namespace StarWarsData.Services.AI.RequestContext;

/// <summary>
/// Request-scoped ambient state pinned by the AGUI envelope-parser middleware
/// before tools execute. Tools read from this to default their filters
/// (continuity, realm, era, etc.) so the agent doesn't have to remember to
/// pass them as parameters. Generalises beyond continuity — see ADR-008 +
/// Design-029.
///
/// Lifetime: scoped — one instance per HTTP request. When no HTTP request is
/// active (background jobs, integration test fixtures that instantiate tools
/// directly), all properties stay null and tools fall back to today's
/// no-filter behaviour. Existing tests pass unchanged.
/// </summary>
public interface ICurrentRequestContext
{
    /// <summary>Active continuity filter. Null = "Both" / no filter.</summary>
    Continuity? Continuity { get; }

    /// <summary>Active realm filter. Null = both Star Wars and Real World.</summary>
    Realm? Realm { get; }

    /// <summary>Page slug from the message envelope, e.g. "galaxy-map".</summary>
    string? Page { get; }

    /// <summary>Page id of the subject from the message envelope, when set.</summary>
    int? SubjectId { get; }
}

/// <summary>
/// Mutable backing record for <see cref="ICurrentRequestContext"/>, registered
/// scoped so the middleware can populate it and tools can read the same
/// instance via the interface. Tests can inject specific values via the
/// fixture and verify filter-aware behaviour without going through HTTP.
/// </summary>
public sealed class CurrentRequestContext : ICurrentRequestContext
{
    public Continuity? Continuity { get; set; }
    public Realm? Realm { get; set; }
    public string? Page { get; set; }
    public int? SubjectId { get; set; }
}
