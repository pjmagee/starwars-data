<!--
Sync Impact Report
==================
Version change: 2.0.0 → 2.1.0 (MINOR — new principle VIII added, no breaking
changes to existing principles)
Modified principles: (none)
Added sections:
  - VIII. SP-4 Integration on Explore Pages — every page under the "Explore"
    nav group MUST register a SubjectKind-bearing PageContext and at least one
    PageControl action, and MUST add a per-page entry in CopilotSidebar's
    SuggestedPrompts switch + HumanizePage label map. Three documented exemptions
    (Search, Knowledge Graph, Graph Explorer) carve out cases where another
    sidebar/panel already owns the agent surface.
Removed sections: (none)
Templates requiring updates:
  - .specify/templates/plan-template.md ⚠ pending (Constitution Check gates remain placeholder;
    populated per-feature by /speckit-plan)
  - .specify/templates/spec-template.md ✅ no change
  - .specify/templates/tasks-template.md ✅ no change
Follow-up TODOs:
  - None.

---- prior history ----
2.0.0 (2026-05-24): V. Engineering Docs Stay in Sync rewritten; eng/design/
                     retired and 45 historical design docs migrated en masse
                     to specs/[NNN]-[slug]/spec.md preserving original numbering.
1.1.0 (2026-05-23): V. Engineering Docs Stay in Sync materially expanded with
                     the durable-vs-per-feature two-layer model and the
                     "MUST NOT migrate" rule (reversed in 2.0.0).
1.0.0 (2026-05-23): initial ratification — 7 principles, Architecture Constraints,
                     Development Workflow, Governance.
-->

# StarWarsData Constitution

## Core Principles

### I. Library-First, Deviations Documented

Standard library components are the default. Third-party libraries adopted in this repo
(MudBlazor, MongoDB.Driver, Microsoft.Extensions.AI, Microsoft.Agents.AI, Hangfire, .NET Aspire,
Keycloak OIDC handlers) MUST be used via their public APIs. Rolling custom HTML/CSS, bespoke
wrappers, or abstractions that bypass the library's intended usage is forbidden by default —
the library almost always has a parameter, variant, or extension point that covers the case.

When a deviation is genuinely justified (the public API cannot meet the requirement), it MUST
be recorded in an ADR under `eng/adr/` *before or alongside* the code change. The ADR entry
must contain: (a) the file/location of the deviation, (b) the library component it replaces,
(c) the concrete reason — with specifics, not "too big" — and (d) a `Revisit when:` line
describing the condition under which the deviation could be removed. New deviations for a
library already covered (e.g. MudBlazor in [eng/adr/004-mudblazor-deviations.md](../../eng/adr/004-mudblazor-deviations.md))
extend that ADR's Catalogue rather than spawning a new one.

**Rationale**: Undocumented deviations silently accumulate, lock the team into bespoke code,
and make library upgrades hazardous. The ADR forces an explicit trade-off and a future exit.
A deviation that is not documented is a bug.

### II. Production Data Safety (NON-NEGOTIABLE)

The `starwars-prod` database is read-only from development tooling, tests, and any agent
context. All development writes — ETL phases, KG rebuilds, Holocron passes, ad-hoc MongoDB
MCP operations — MUST target `starwars-dev`. The `Settings:DatabaseName` configuration
defaults to `starwars-dev`; production deploys override via `appsettings.json` or env var
`Settings__DatabaseName` only on the production host.

Credentials for MongoDB MUST be resolved from the host's `MDB_MCP_CONNECTION_STRING` env var
(MCP usage) or from Aspire AppHost user-secrets parameters (`Parameters:mongo-user`,
`Parameters:mongo-password`, `Parameters:mongo-host`, `Parameters:mongo-port`). Hardcoded
connection strings, split credential pairs across secret/var tiers, or constructing
connection strings inline are forbidden.

**Rationale**: A single accidental write to `starwars-prod` from dev tooling is a category
of incident this constitution exists to prevent. The default-dev posture means the failure
mode of a misconfigured tool is "wrote to dev", never "corrupted prod".

### III. Test Tiering & Pre-Commit Gate

Every test in `src/StarWarsData.Tests` MUST be tagged with exactly one of
`[TestCategory(TestTiers.Unit)]`, `[TestCategory(TestTiers.Integration)]`, or
`[TestCategory(TestTiers.Agent)]` and live in the matching `Unit/`, `Integration/`, or
`Agent/` folder. The tiers carry hard contracts:

- **Unit** — pure logic. No Docker, no env vars, no network, no Testcontainers, no OpenAI.
  This tier is the pre-commit gate; it MUST pass in under a few seconds on a cold machine.
- **Integration** — Testcontainers MongoDB only. Runs in CI when Docker is available.
- **Agent** — real OpenAI key + live `starwars-dev` MongoDB. Manual / nightly only;
  MUST NOT run in pre-commit or default CI.

Fixtures live under `src/StarWarsData.Tests/Infrastructure/` and MUST be lazy-static (an
`EnsureInitializedAsync()` plus `[ClassInitialize]` wiring). Unit-only runs MUST NOT spin
up any container or contact OpenAI — verified by running the Unit filter on a machine with
Docker disabled.

**Rationale**: A pre-commit gate that pays Docker startup or OpenAI cost is one developers
will silently disable. Tier discipline keeps the fast path fast and the expensive path
opt-in. See the matrix in [CLAUDE.md](../../CLAUDE.md) for filter syntax.

### IV. UI Changes Validated in a Browser (NON-NEGOTIABLE)

Any change that touches rendered UI — Frontend or Admin pages, layouts, shared components,
theming, `wwwroot/` CSS/JS, JS interop, MudBlazor parameter swaps, scoped `.razor.css` —
MUST be validated against a running browser via the Chrome DevTools MCP
(`mcp__chrome-devtools__*`) before the work is reported complete. Type-check passing and a
successful build are necessary but NOT sufficient — they verify code correctness, not
feature correctness.

The validation loop is iterative, not a one-shot end-of-task check:

1. After each meaningful UI change, navigate to the affected page.
2. Snapshot the DOM and confirm the markup matches the intent.
3. Read the console for Blazor circuit drops, JS interop errors, MudBlazor warnings —
   fix them, do not accept them.
4. Resize to mobile (414×896) and re-snapshot if the change touches layout.
5. Take a screenshot for the report-back and cite the URL.

If a running AppHost is not available (port collision the agent cannot resolve, auth gate
the agent cannot pass), the report-back MUST say so explicitly. Silent omission of
validation is forbidden. This rule applies to *every* agent that edits a UI-affecting file,
not only the `blazor-mudblazor-expert` sub-agent.

**Rationale**: This rule exists because we have observed UI regressions ship past green
type-checks. The cost of running Chrome DevTools MCP is seconds; the cost of a broken UI
on `main` is hours of triage.

### V. Engineering Docs Stay in Sync

`eng/adr/`, `eng/docs/`, `eng/diagrams/`, and `specs/` are load-bearing — not archival.
When a code change alters a decision, architecture, or workflow captured in those
locations, the corresponding document MUST be updated in the same PR as the code
change. A doc that contradicts the code is a bug.

**Two artifact roots, one direction of travel:**

- `eng/adr/`, `eng/docs/`, `eng/diagrams/` — **durable institutional knowledge** that
  outlives any single feature. Cross-cutting decisions (ADRs), contributor how-to
  guides (docs), and the architecture model (diagrams). These DO NOT carry feature
  status or ship dates; they describe rules / patterns / how-to that apply across
  features.
- `specs/[NNN-slug]/` — **per-feature artifacts** owned by spec-kit. Each numbered
  directory contains `spec.md` (user stories or a historical design narrative,
  status tracked from Proposed → Shipped → Superseded), and optionally `plan.md`,
  `tasks.md`, `research.md`, `data-model.md`, `quickstart.md`, `contracts/`,
  `checklists/`, `screenshots/`. New feature work uses the spec-kit workflow
  (`/speckit-specify` → `/speckit-plan` → `/speckit-tasks` → `/speckit-implement`);
  historical work imported from `eng/design/` may only have `spec.md`.

**`eng/design/` was retired in v2.0.0.** All 45 historical design docs were migrated
en masse to `specs/[NNN]-[slug]/spec.md` preserving their original numbering. The
`Design-NNN` shorthand still refers to the same body of work; the docs just live
under `specs/` now. There is no longer a parallel feature-artifact root in `eng/`.

Spec-kit artifacts MUST cite relevant `eng/adr/N` entries (and other `specs/N/`
features they depend on) as binding constraints — typically in the plan's Technical
Context or the Constitution Check section. When a feature crystallises a new
standing decision that applies to *all* future features, that decision MUST graduate
out of the per-feature `plan.md` and into a new ADR under `eng/adr/`. `eng/adr/` is
where rules for all future features live; never inside a single feature's `specs/`
folder.

- **ADRs are immutable**: changes supersede via a new numbered ADR; never rewrite
  history.
- **Specs track status**: each `specs/[N]/spec.md` has a Status field — keep it
  current as phases ship.
- **LikeC4 model** (`eng/diagrams/*.c4`) MUST be updated when components, their
  relationships, or deployment shape change.
- When an ADR or feature spec establishes a rule an agent must follow, a reference
  to it MUST be added from the relevant section of [CLAUDE.md](../../CLAUDE.md).

**Rationale**: The v1.x partition between `eng/design/` (alleged "durable") and
`specs/` ("per-feature") was aspirational, not descriptive. In practice ~80% of
`eng/design/` docs were scoped feature plans with phases, ship dates, and the same
shape as spec-kit artifacts — making the partition meaningless and confusing for
contributors choosing where to put new feature work. The v2.0.0 migration unifies
on `specs/` for feature work, keeps `eng/adr/`, `eng/docs/`, `eng/diagrams/` for
genuinely durable knowledge (rules, how-to, architecture model), and removes the
parallel root that was duplicating effort.

### VI. KG-First Data Access at Runtime

Runtime services (ApiService, Frontend, Admin runtime pages) MUST read from `kg.nodes`,
`kg.edges`, and their derived views — never from `raw.pages` or `raw.*` collections.
The ETL pipeline writes to `raw.*` and produces `kg.*`; runtime consumes `kg.*` only.

Missing or malformed fields MUST be fixed at the ETL source (the appropriate node builder
under `Services/AI/KnowledgeGraph/NodeBuilders/`), never derived, defaulted, or constructed
downstream in a service or controller. Soft-handling is acceptable only where the data is
genuinely absent at source (e.g. ~0.05% kg.nodes with `contentHash: null` from stub
infoboxes — skip the comparison, do not flag stale).

**Rationale**: Runtime reads from raw data silently bypass the KG's edge quality,
provenance, and Holocron enrichment work. Downstream field construction creates two
sources of truth and turns every consumer into a partial re-implementation of the ETL.

### VII. Global Filter Respect

Every content-bearing Frontend page and component that queries the API MUST respect the
global filter (continuity: Canon/Legends, realm: Star Wars/Real) by subscribing to
`GlobalFilterService.OnChange` and passing the filter values via `GetContinuityQueryParam()`
/ `GetRealmQueryParam()` to API calls. Active queries and rendered data MUST refresh when
the filter changes.

Continuity chips and badges MUST use the canonical MudBlazor theme colors:
`Continuity.Canon → Color.Primary`, `Continuity.Legends → Color.Secondary`, otherwise
`Color.Default`. `Color.Info`/`Color.Warning`/etc. for continuity are forbidden. See
`ContinuityBadge.razor` and `ContinuityFilter.razor` as canonical references.

**Documented exemption**: the public corpus-stats surface (`/api/stats/*` and the Frontend
"Miscellaneous" section, per [specs/037-misc-site-activity-dashboard/spec.md](../../specs/037-misc-site-activity-dashboard/spec.md))
is deliberately filter-exempt because it reports whole-corpus *infrastructure* health, not
continuity-scoped content. This carve-out is bounded and authoritative per
[eng/adr/009-public-readonly-corpus-stats-surface.md](../../eng/adr/009-public-readonly-corpus-stats-surface.md);
it does NOT generalise.

**Rationale**: Users expect the filter chip to mean what it says everywhere it is visible.
Per-page filter forgetfulness is invisible until users notice content leaking across
continuities — the discovery cost is high and trust is hard to restore.

### VIII. SP-4 Integration on Explore Pages

Every page under the **Explore** group in `NavMenu.razor` MUST integrate with SP-4 (the
copilot sidebar) so the user can ask questions about — and act on — what they're currently
looking at. Concretely:

1. **Page context**: Inject `PageContextService` and call `PageContext.Set(new PageContext(...))`
   on `OnInitialized` and again on every relevant state change (selection, filter, tab swap).
   `PageContext.Clear()` MUST be called in `Dispose` / `DisposeAsync`. The `Page` slug
   matches the route segment (e.g. `"family-trees"`, `"galaxy-map"`). When the user has
   focused on a specific entity, populate `Subject` + `SubjectKind` + `SubjectId` so the
   sidebar's per-subject suggestion arm fires.
2. **Page tools**: Inject `PageControlService` and call `PageControl.Register(page, actions)`
   from `OnAfterRender(firstRender)` with at least one `PageAction`. Tool names use the
   `<page>_<verb>` convention (e.g. `family_trees_select_family`,
   `knowledge_graph_set_node_filters`). The registration token is disposed in `Dispose`.
3. **Sidebar wiring**: Add a `case` to `CopilotSidebar.SuggestedPrompts()` for the new page
   (page-level + per-`SubjectKind` arms), define the prompt arrays, and add the page slug
   to the `HumanizePage` label map. Generic prompts on a content page indicate missing
   integration.

**Documented exemptions** (each carves a narrow, named hole; do not generalise):

- **`/search`** — the search input IS the agent surface; a parallel SP-4 conversation panel
  would compete with the primary input.
- **`/knowledge-graph`** — already fully integrated (it was the reference implementation);
  noted here because the principle applies *as a check*, not a future task.
- **`/graph-explorer`** — D3 canvas owns the entire viewport; the sidebar is hidden by
  CSS on this page deliberately (see `MainLayout.razor`). PageContext is still published
  so the copilot has context when the user opens it elsewhere.

**Rationale**: SP-4 was the entire point of routing the agent through the page (Design-041);
a content page that doesn't publish what the user is looking at silently degrades the agent
to a generic FAQ. Users notice when they highlight a node and the sidebar still suggests
"Tell me something interesting about Star Wars" — it reads as broken, not minimal. Treating
this as a per-page chore that "we'll get to next" leaves a permanent gap; making it part of
the Explore-page contract closes it at the source.

## Architecture Constraints

- **AI stack**: Microsoft.Extensions.AI (`IChatClient`) + Microsoft.Agents.AI (`AIAgent`,
  `AITool`) + OpenAI SDK. **Semantic Kernel is forbidden** — do not add SK packages.
- **Agent classes** live under `Services/AI/Agents/<Agent>/`. Agent-scoped toolkits live at
  `Services/AI/Agents/<Agent>/Tools/`; only cross-agent toolkits stay at
  `Services/AI/Toolkits/`. Tool name constants always go in `ToolNames.cs`.
- **MongoDB**: single database (`Settings.DatabaseName`) with namespaced collections
  (`raw.*`, `timeline.*`, `kg.*`, `search.*`, `genai.*`, `chat.*`, `territory.*`,
  `galaxy.*`, `admin.*`, `hangfire.*`). Production = `starwars-prod`, development =
  `starwars-dev`.
- **Authentication**: Keycloak OIDC on the Frontend. The API is internal-only; user identity
  is forwarded via the `X-User-Id` header set by a `DelegatingHandler` from the authenticated
  `ClaimsPrincipal`. Rationale in [eng/adr/001-internal-api-auth.md](../../eng/adr/001-internal-api-auth.md).
- **Aspire orchestration**: when working with Aspire APIs, configuration, or orchestration
  patterns, the Aspire docs MUST be consulted via the Aspire MCP (`mcp__aspire__search_docs`,
  `mcp__aspire__get_doc`) before guessing at APIs. Agents that need to boot the AppHost use
  `aspire run --isolated --detach`; never use `--isolated` with `prepare-starwars`/`deploy`.
- **CI publish path**: public-repo CI uses `aspire publish` (template only). It MUST NOT
  use `prepare-starwars` or `aspire deploy`, both of which resolve user-secrets and would
  bake credentials into release artifacts.
- **GDPR compliance**: cookie consent banner (blocks Google Analytics until accepted),
  Privacy Policy at `/privacy`, Terms of Use at `/terms`, "Delete All My Data" and
  "Export My Data" in Profile.
- **Folder convention**: ApiService, Admin, and Services use feature-based organisation
  (`Features/<FeatureName>/` or `<FeatureName>/`), not layer-based (no top-level
  `Controllers/`, `Repositories/`).

## Development Workflow

- **Branch model**: each feature gets its own branch via `/speckit-git-feature`. The
  spec-kit flow is `/speckit-constitution` (this file) → `/speckit-specify` →
  optional `/speckit-clarify` → `/speckit-plan` → `/speckit-tasks` → optional
  `/speckit-checklist` and `/speckit-analyze` → `/speckit-implement`.
- **Pre-commit gate**: Unit tier MUST pass. `dotnet test --project src/StarWarsData.Tests
  --filter "TestCategory=Unit"` is the minimum bar.
- **CI gate**: Unit + Integration tiers. Agent tier is excluded by default.
- **ETL operations**: trigger pipeline phases via the Aspire admin resource HTTP commands
  (1a–9) or the Admin app's controllers — never by manual MongoDB writes that bypass the
  builders.
- **Library deviations**: require an ADR entry per Principle I before merge.
- **UI changes**: require Chrome DevTools MCP validation per Principle IV before report-back.
- **Doc sync**: any PR that changes an architectural decision, workflow, or component
  relationship MUST include the corresponding `eng/` doc update.

## Governance

This constitution supersedes ad-hoc practice. When this document and a memory file,
comment, or chat message disagree, this document wins until amended.

**Amendment procedure**:

1. Propose the change in a PR that modifies `.specify/memory/constitution.md` directly.
2. Bump `Version` per semantic versioning:
   - **MAJOR**: backward-incompatible removal or redefinition of a principle.
   - **MINOR**: new principle or materially expanded guidance.
   - **PATCH**: clarifications, wording, typo fixes, non-semantic refinements.
3. Update `Last Amended` to today (ISO YYYY-MM-DD). `Ratified` is immutable.
4. Prepend a Sync Impact Report HTML comment at the top describing what changed and
   which downstream templates / docs need updates.
5. Update [CLAUDE.md](../../CLAUDE.md) if the principle changes runtime agent guidance.

**Compliance**:

- PRs and code reviews MUST verify compliance with the principles above. Reviewers cite
  the principle by name (e.g. "Principle IV: needs Chrome DevTools validation").
- Complexity that conflicts with a principle MUST be justified in the `Complexity Tracking`
  table of the feature's `plan.md`.
- Runtime agent guidance for day-to-day work lives in [CLAUDE.md](../../CLAUDE.md) and the
  per-domain skills in `.claude/skills/`. Those files are subordinate to this constitution.

**Version**: 2.0.0 | **Ratified**: 2026-05-23 | **Last Amended**: 2026-05-24
