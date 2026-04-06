# ADR-002: Three-Project Blazor Server + Shared API with Cross-Project Vertical Slices

**Status:** Accepted
**Date:** 2026-04-05
**Decision maker:** Patrick Magee

## Context

Star Wars Data Explorer is an Aspire-orchestrated .NET 10 solution with three runtime apps plus shared libraries:

- **StarWarsData.Admin** — Blazor Web App, Interactive Server render mode. LAN-only (no public routing). Hosts Hangfire dashboard, ETL pipeline controls, admin tooling.
- **StarWarsData.ApiService** — ASP.NET Core Web API. Hosts the AI agent (Microsoft.Agents.AI + AGUI streaming), MongoDB MCP client, feature endpoints, rate limiting, BYOK key lookup.
- **StarWarsData.Frontend** — Blazor Web App, Interactive Server render mode. Public-facing, authenticates via Keycloak OIDC (see ADR-001).
- **StarWarsData.Services / .Models / .ServiceDefaults** — shared class libraries referenced by the three runtime projects.

Both Blazor Server apps call ApiService server-to-server over HTTP. The Admin app and Frontend app do **not** share a Blazor host process.

We needed to decide, and document, two things:

1. Is a "two Blazor Server apps + shared external Web API" topology a sensible pattern, or are we paying for an HTTP hop we don't need?
2. Is feature/vertical-slice organization valid when a single slice physically spans four projects (`Services`, `ApiService`, `Admin`, `Frontend`, `Models`)?

## Decision

**We adopt and standardize the three-runtime topology with cross-project vertical slices.**

### Topology

```
┌─────────────────┐       ┌─────────────────┐
│  Frontend       │       │  Admin          │
│  Blazor Server  │       │  Blazor Server  │
│  (Keycloak)     │       │  (LAN only)     │
└────────┬────────┘       └────────┬────────┘
         │ HTTP                    │ HTTP
         │ X-User-Id               │ (system identity)
         ▼                         ▼
         ┌───────────────────────────┐
         │  ApiService               │
         │  AI agent, MCP, endpoints │
         └────────────┬──────────────┘
                      │
                      ▼
         ┌───────────────────────────┐
         │  MongoDB (self-hosted)    │
         └───────────────────────────┘

All three runtime apps reference:
  StarWarsData.Services  (domain logic, in-proc)
  StarWarsData.Models    (entities + DTOs)
  StarWarsData.ServiceDefaults (OTel, resilience)
```

### Slice organization

A feature slice is a **logical concept that spans projects**, co-located under the same feature name in each project:

| Project | Slice location |
| --- | --- |
| `StarWarsData.Models` | `Models/<Feature>/` — DTOs + entities (shared contracts) |
| `StarWarsData.Services` | `Services/<Feature>/` — domain logic, queries, workflows |
| `StarWarsData.ApiService` | `Features/<Feature>/` — HTTP endpoints for that slice |
| `StarWarsData.Admin` | `Features/<Feature>/` — admin controllers/pages for that slice |
| `StarWarsData.Frontend` | `Components/Pages/<Feature>*.razor` + `Components/Shared/<Feature>/` |

No horizontal layer folders (`Controllers/`, `Repositories/`, `ViewModels/`) at a project root. The recent restructure (commit `c6eccae7c`) already aligned `Services` and `ApiService` with this convention.

## Rationale

### Why three runtime projects and not one?

Microsoft's official guidance in [Call a web API from ASP.NET Core Blazor](https://learn.microsoft.com/aspnet/core/blazor/call-web-api) cautions:

> "Injecting an HttpClient on the server that makes calls back to the server isn't recommended, as the network request is typically unnecessary."

That warning applies when the API lives in the **same process** as the Blazor Server app. Our situation is different on every axis:

1. **Multiple consumers.** Admin and Frontend both need the same data and AI capabilities. Duplicating that logic into each Blazor host would violate DRY and split the AI agent lifecycle.
2. **Different trust zones.** Frontend is public and auth'd via Keycloak; Admin is LAN-only with an implicit trust boundary. Collapsing them into one process forces a single auth story.
3. **Different scaling / deployment profiles.** The AI agent, MCP sidecar, OpenAI client, and Hangfire sync/ingest workloads have independent resource and uptime profiles from the public UI.
4. **Aspire is the orchestrator.** The framework is designed around multiple co-deployed resources with HTTP commands, logs, and traces per resource. Our ETL pipeline HTTP commands on the `admin` resource are a first-class Aspire feature.
5. **.NET architecture guidance explicitly endorses the split.** From [Develop ASP.NET Core MVC apps § APIs and Blazor applications](https://learn.microsoft.com/dotnet/architecture/modern-web-apps-azure/develop-asp-net-core-mvc-apps#apis-and-blazor-applications):
   > "If your application includes a set of web APIs, which must be secured, these APIs should ideally be configured as a separate project from your View or Razor Pages application. Separating APIs, especially public APIs, from your server-side web application has a number of benefits. These applications often will have unique deployment and load characteristics. They're also very likely to adopt different mechanisms for security…"

We still honor the MS warning **within a process**: any logic that doesn't need centralization lives in `StarWarsData.Services` and is called via DI directly from Admin or Frontend — not via an HTTP round-trip to ApiService.

### Why vertical slices across projects?

Microsoft's [Feature Slices guidance](https://learn.microsoft.com/dotnet/architecture/modern-web-apps-azure/develop-asp-net-core-mvc-apps#structuring-the-application):

> "Large applications may encounter problems with [layered] organization, since working on any given feature often requires jumping between these folders… One solution is to organize application code by *feature* instead of by file type. This organizational style is typically referred to as feature folders or feature slices."

The benefit compounds when features cross project boundaries. When a developer changes "GalaxyMap", everything they need is at `*/GalaxyMap/` in each project — no scrolling through layered folders in four different solutions. The physical project boundary is a **deployment concern**, not an organizational one.

## Rules

1. **Feature naming must match across projects.** If the slice is `GalaxyMap`, every project uses `GalaxyMap` — not `Map` here and `Galaxy` there.
2. **Contracts live in `Models/<Feature>/`.** This is the only project all three runtimes reference. Admin and Frontend must not each define their own DTO shape for the same feature.
3. **No cross-slice reaching.** `Features/Chat/` must not `using` types from `Features/KnowledgeGraph/` except through a well-defined seam in `Services/Shared/` or a public contract in `Models/`.
4. **Prefer in-process DI over HTTP where possible.** If Admin can call a `Services/<Feature>/` class directly, do that. Only route through ApiService when centralization is required (AI agent, MCP, rate limiting, BYOK keys, per-user state).
5. **Shared infrastructure is not a slice.** Auth handlers, telemetry, resilience, Aspire defaults live in `ServiceDefaults` / `Shared/`, not under a feature folder.
6. **Typed HTTP clients.** Both Admin and Frontend call ApiService through typed `HttpClient` wrappers (per [Blazor typed HttpClient guidance](https://learn.microsoft.com/aspnet/core/blazor/call-web-api#typed-httpclient)). If we see two near-identical wrappers emerging, extract them into a shared `StarWarsData.ApiClient` library.
7. **Auth asymmetry is explicit.** Frontend forwards user identity via `X-User-Id` (ADR-001). Admin calls use a system identity or hit endpoints that don't require a user. Endpoints requiring a user must reject unauthenticated calls regardless of source.

## Consequences

### Positive

- Admin and Frontend evolve independently with their own deployment cadence and auth posture.
- Centralized AI agent, MCP client, and rate limiting — one place to change, one place to observe.
- Vertical slices make feature work local: one mental model, co-located files.
- Aspire dashboard gives per-resource logs, traces, and HTTP commands out of the box.
- Shared `Services` library prevents unnecessary HTTP hops for pure domain logic.

### Negative / costs to manage

- Network hop for Frontend→ApiService and Admin→ApiService calls (acceptable — both are intra-cluster).
- Risk of drifting DTOs if the `Models/` contract discipline slips.
- Risk of duplicate HttpClient wrappers in Admin and Frontend; mitigate by extracting a shared client library when duplication appears.
- Vertical slices spanning 4+ projects require disciplined naming during rename/refactor — enforce in code review.

### Out of scope / rejected alternatives

- **Single Blazor Server app hosting the API in-proc.** Rejected: two consumers with different trust zones and the MS warning does not argue for collapsing when consumers differ.
- **Blazor WebAssembly for Frontend.** Rejected: ADR-001 established Interactive Server mode for auth and SEO reasons.
- **Layered folders (`Controllers/`, `Repositories/`).** Rejected: the commit `c6eccae7c` restructure already moved to feature slices and this ADR locks that in.
- **Direct DB access from Admin/Frontend bypassing ApiService.** Allowed only for pure domain logic via `Services/`. Anything needing user identity, rate limits, AI agents, or MCP must go through ApiService.

## References

- ADR-001: Internal API Authentication via X-User-Id Header
- [Call a web API from ASP.NET Core Blazor](https://learn.microsoft.com/aspnet/core/blazor/call-web-api)
- [Develop ASP.NET Core MVC apps — APIs and Blazor applications](https://learn.microsoft.com/dotnet/architecture/modern-web-apps-azure/develop-asp-net-core-mvc-apps#apis-and-blazor-applications)
- [Feature Slices for ASP.NET Core MVC](https://learn.microsoft.com/archive/msdn-magazine/2016/september/asp-net-core-feature-slices-for-asp-net-core-mvc)
- [ASP.NET Core Blazor project structure](https://learn.microsoft.com/aspnet/core/blazor/project-structure)
