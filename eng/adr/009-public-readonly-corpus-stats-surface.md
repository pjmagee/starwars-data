# ADR-009: Public read-only corpus-stats surface served by the ApiService

**Status:** Accepted
**Date:** 2026-05-18
**Decision maker:** Patrick Magee
**Cross-refs:** [ADR-001 Internal API Auth](001-internal-api-auth.md), [ADR-002 Three-Project Blazor Server + Shared API](002-three-project-blazor-server-shared-api.md), [Design-037 Miscellaneous site-activity dashboard](../design/037-misc-site-activity-dashboard.md)

## Context

We want curious public visitors to the **Frontend** site to see at a glance that the corpus is alive and maintained: total KG node/edge counts, total wiki article pages and article chunks, when the daily wiki sync last ran, and the most recently synced / chunked articles. See [Design-037](../design/037-misc-site-activity-dashboard.md) for the feature and UX.

This raises three cross-cutting questions that outlive the feature itself:

1. **Where do the numbers come from?** The data lives in `kg.nodes`, `kg.edges`, `raw.pages`, `search.chunks`, `raw.job_state`. The **Admin** app already renders similar numbers, but Admin is a *separate, internal-only Blazor app* ([ADR-002](002-three-project-blazor-server-shared-api.md)) — it is not internet-exposed and the public Frontend cannot call it. The Frontend never talks to MongoDB directly; it only calls the ApiService over the `X-User-Id`-stamped HttpClient ([ADR-001](001-internal-api-auth.md)).
2. **How fresh must the numbers be, and what does that cost?** These are corpus-scale collections (hundreds of thousands of docs each, on a shared self-hosted `mongod` that also serves production). A naive exact `countDocuments()` per page-load, multiplied by every curious anonymous visitor, is unbounded read load against the same instance the live site depends on.
3. **Does the global continuity/realm filter apply?** [CLAUDE.md](../../CLAUDE.md) mandates that *every page and component that queries the API must respect the global filter*. That rule exists for **content** queries (timelines, graph, search) where Canon vs Legends materially changes the answer.

## Decision

**1. The stats surface is a new public, read-only feature of the ApiService — not Admin, not direct Mongo.**
A new `Features/Stats/StatsController.cs` exposes `GET /api/stats/*` endpoints backed by a new `CorpusStatsService` (`Services/Stats/`). The Frontend consumes it exactly like every other page — `IHttpClientFactory.CreateClient("StarWarsData")`, through the existing `UserIdDelegatingHandler`. No Admin coupling; no new MongoDB access path from the Frontend. The endpoints require **no authentication** — they are public-information endpoints in the same class as `/about` and the existing `/api/costs/*` surface that powers the public `/costs` page, exposing only aggregate counts and public article titles (already-public Wookieepedia data), never user or operational data.

**2. The numbers are a server-side cached snapshot, not a live per-request count.**
`CorpusStatsService` computes one snapshot (collection counts + recent lists + last-sync timestamp) and caches it in `IMemoryCache` with a short TTL (default **5 minutes**). All public traffic in a TTL window is served from memory; at most one refresh per TTL touches MongoDB. Collection totals use `EstimatedDocumentCountAsync` (O(1) metadata read), not `CountDocumentsAsync` — exact precision is irrelevant to an "is the site alive" signal, and the estimate cannot be made to scan the corpus. Recent-item lists are bounded `Find().Sort().Limit(≤25)` queries on indexed timestamp fields.

**3. The stats surface is explicitly exempt from the global continuity/realm filter.**
It reports corpus *infrastructure* health (how much data exists, when sync last ran), not continuity-scoped *content*. Numbers are whole-corpus totals and are continuity-agnostic. The Miscellaneous pages do **not** subscribe to `GlobalFilterService.OnChange` and do **not** pass `GetContinuityQueryParam()` / `GetRealmQueryParam()`. This is a deliberate, bounded carve-out from the CLAUDE.md global-filter rule, scoped strictly to the `/api/stats/*` endpoints and the Miscellaneous nav section.

## Alternatives considered

- **Reuse the Admin endpoints / Admin Dashboard.** Rejected: Admin is internal-only and unreachable from the public Frontend ([ADR-002](002-three-project-blazor-server-shared-api.md)); exposing Admin to the internet to share a stats widget would invert that decision and widen the attack surface for a cosmetic feature.
- **Query MongoDB directly from the Frontend.** Rejected: the Frontend has never held a Mongo connection; all data access is via the ApiService ([ADR-001](001-internal-api-auth.md)). Introducing a Frontend→Mongo path for one dashboard is a large architectural regression.
- **Live exact `countDocuments()` per request.** Rejected: unbounded, anonymous, repeatable read load (full collection scans without a covering predicate) against the shared production `mongod`. The "alive" signal does not need exactness or sub-5-minute freshness.
- **Bake stats into a Hangfire job that writes a `stats` collection.** Rejected as the v1: more moving parts (a new recurring job, a new collection, a migration) than an in-process cache for a value nobody needs to be durable. Revisit if multi-instance ApiService scaling makes per-instance caches diverge visibly (see *Revisit when*).
- **Apply the global filter to the stats.** Rejected: continuity-partitioned counts would make the dashboard answer "how much Canon data exists" rather than "is the pipeline working", which is the actual user need; it also doubles the query cost for no signal.

## Consequences

- One new public, unauthenticated, read-only controller (`Features/Stats`) and one cached service (`Services/Stats/CorpusStatsService`). New DTOs under `StarWarsData.Models.Stats`.
- Public stats can lag reality by up to the cache TTL. Acceptable and intended; the dashboard copy should say "updated periodically", not present live figures.
- MongoDB exposure is one estimated-count batch + a few small indexed `Find`s per TTL per ApiService instance, regardless of public traffic volume.
- A documented exemption from the global-filter rule now exists. To keep it from eroding silently, CLAUDE.md's *Global Filter* section links here; the exemption is valid **only** for `/api/stats/*` + the Miscellaneous section. Any new content-bearing page remains bound by the rule.
- `CorpusStatsService` must own its own thin Mongo access (counts + recent `Find`s) rather than depending on heavy services like `PageDownloader`/`InfoboxGraphService`, to keep the public path cheap and free of admin/ETL surface area.

## Revisit when

- The ApiService is scaled to **multiple instances** and per-instance 5-minute caches visibly disagree (e.g. counts flicker between refreshes across a load balancer). Then promote the snapshot to a single Hangfire-written `admin.corpus_stats` document read by all instances (the rejected "baked job" alternative becomes correct).
- A future requirement genuinely needs continuity-scoped corpus numbers — at that point the global-filter exemption must be re-evaluated for those specific figures, not blanket-removed.
