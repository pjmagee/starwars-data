# CLAUDE.md

This file is the **index** for Claude Code in this repo. Binding rules live in the **constitution** at [.specify/memory/constitution.md](.specify/memory/constitution.md) (7 numbered principles); decisions and designs live under [eng/](eng/). CLAUDE.md points at them and surfaces only per-turn operational reminders (commands, MCP names, skill names) — when CLAUDE.md and the constitution disagree, the constitution wins.

**New here?** [ONBOARDING.md](ONBOARDING.md) gets a fresh clone running against real data (snapshot restore, no ETL re-run, only your own OpenAI key needed). Mechanism: [specs/038-developer-onboarding-snapshot/spec.md](specs/038-developer-onboarding-snapshot/spec.md).

## Spec-Kit Workflow

This repo uses [GitHub Spec Kit](https://github.com/github/spec-kit) for feature work. Per-feature artifacts live under `specs/[###-name]/`. Use the `/speckit-*` skills in order:

1. `/speckit-constitution` — amend [.specify/memory/constitution.md](.specify/memory/constitution.md) (rare; cross-cutting only).
2. `/speckit-git-feature` — create the feature branch + `specs/[###-name]/` folder.
3. `/speckit-specify` → `/speckit-clarify` *(optional)* — write `spec.md` (user stories, acceptance, success criteria).
4. `/speckit-plan` — write `plan.md` (technical context, structure, Constitution Check). Cite binding `eng/adr/N` entries (and other `specs/M/spec.md` features) here.
5. `/speckit-tasks` → `/speckit-checklist` / `/speckit-analyze` *(optional)* — generate ordered work.
6. `/speckit-implement` — execute `tasks.md`.
7. `/speckit-taskstoissues` *(optional)* — push tasks to GitHub Issues.

**Two artifact roots** (Principle V, constitution v2.0.0):

- `specs/[NNN-slug]/` — **per-feature** artifacts. Each numbered dir has `spec.md` (user stories or a historical design narrative, Status tracked); optionally `plan.md`, `tasks.md`, `research.md`, `data-model.md`, `quickstart.md`, `contracts/`, `checklists/`, `screenshots/`. New feature work uses spec-kit; historical work imported from the retired `eng/design/` (May 2026) keeps its NNN number and may only have `spec.md`.
- `eng/adr/`, `eng/docs/`, `eng/diagrams/` — **durable institutional** knowledge (cross-feature decisions, how-to guides, architecture model).

When a per-feature plan crystallises a new standing rule, **graduate it into `eng/adr/`** — never leave a rule for future features buried in one feature's `plan.md`.

## Authoritative References

| Subject | Where the rule/design lives | One-line restatement |
| --- | --- | --- |
| All binding rules | [.specify/memory/constitution.md](.specify/memory/constitution.md) | 7 principles; overrides chat and memory until amended. |
| Library deviations | Principle I + [eng/adr/004-mudblazor-deviations.md](eng/adr/004-mudblazor-deviations.md) | Standard library APIs are default; deviations need an ADR entry with `Revisit when:`. |
| Production DB safety | Principle II + [eng/adr/010-aspire-managed-dev-mongo.md](eng/adr/010-aspire-managed-dev-mongo.md) | Default DB `starwars-dev`; never write to `starwars-prod` from dev tooling. |
| Test tiers | Principle III + [src/StarWarsData.Tests/TestTiers.cs](src/StarWarsData.Tests/TestTiers.cs) | `Unit` (pre-commit), `Integration` (CI), `Agent` (manual/nightly). |
| UI validation | Principle IV | Every UI-touching change MUST be validated via Chrome DevTools MCP before report-back. |
| KG-first runtime | Principle VI | Runtime reads `kg.*` only; fix missing fields at the ETL node-builder source. |
| Global filter + continuity colours | Principle VII + [eng/adr/009-public-readonly-corpus-stats-surface.md](eng/adr/009-public-readonly-corpus-stats-surface.md) | Every content page subscribes to `GlobalFilterService.OnChange`. Canon→`Primary`, Legends→`Secondary`. |
| SP-4 on Explore pages | Principle VIII (constitution v2.1.0) | Every Explore-group page registers a `PageContext` (with `SubjectKind` on selection), at least one `PageControl` action, and a `CopilotSidebar.SuggestedPrompts` case. Documented exemptions: `/search`, `/knowledge-graph` (reference impl), `/graph-explorer`. |
| Internal API auth | [eng/adr/001-internal-api-auth.md](eng/adr/001-internal-api-auth.md) | Keycloak OIDC on Frontend; `X-User-Id` header to the internal API. |
| Aspire publish/deploy | [specs/017-aspire-publish-deploy-workflow/spec.md](specs/017-aspire-publish-deploy-workflow/spec.md) | `publish` (template) vs `prepare-starwars` (filled) vs `deploy`. |
| Running Aspire from an agent | [eng/docs/aspire-isolated-mode-for-claude-code.md](eng/docs/aspire-isolated-mode-for-claude-code.md) | Use `aspire run --isolated --detach`; never with `prepare`/`deploy`. |
| Holocron LLM enrichment | [specs/018-kg-enrichments-architecture/spec.md](specs/018-kg-enrichments-architecture/spec.md) + [specs/020-holocron-async-pipeline/spec.md](specs/020-holocron-async-pipeline/spec.md) | Separate pass over `kg.*`; not an ETL phase. |
| AGUI page control (SP-4) | [specs/041-sp-4-page-control-agui-frontend-tools/spec.md](specs/041-sp-4-page-control-agui-frontend-tools/spec.md) | Frontend tools dispatched via official `Microsoft.Agents.AI.AGUI` client. |
| SP-4 global tool family | [specs/043-sp4-global-tool-family/spec.md](specs/043-sp4-global-tool-family/spec.md) | `sp4_*` prefix for tools available everywhere the sidebar mounts (sibling to per-page `<page>_*`). First member: Wookieepedia article modal. |
| Family tree rendering | [specs/042-family-tree-component/spec.md](specs/042-family-tree-component/spec.md) | Kinship phrasing on `/ask` routes to `render_family_tree` (NOT `render_graph`). Renderer is vendored [family-chart-premium](https://github.com/donatso/family-chart-premium) under `wwwroot/lib/`; non-commercial licence, watermark kept. Endpoint at `GET /api/RelationshipGraph/family-tree/{pageId}`; Gender from `kg.nodes.properties["Gender"]` (Principle VI). |

## Engineering Docs (`eng/`)

The `eng/` folder is **load-bearing, not archival**. A doc that contradicts the code is a bug — update it in the same PR as the code change.

| Folder | Purpose |
| --- | --- |
| `eng/adr/` | Architecture Decision Records — numbered, immutable. Supersede; don't rewrite. |
| `eng/docs/` | How-to / reference guides. |
| `eng/diagrams/` | LikeC4 model — update when components or deployment shape change. Syntax: `/likec4-dsl` skill. |
| `eng/scripts/` | Engineering/ops scripts (see its `README.md`). |
| `eng/NOTES.md` | Scratch list of external references. |

> Per-feature design narratives that used to live in `eng/design/` were migrated to `specs/[NNN-slug]/spec.md` in v2.0.0 (constitution amendment 2026-05-24) — see the [.specify/memory/constitution.md](.specify/memory/constitution.md) Sync Impact Report for the rationale. The `Design-NNN` shorthand still refers to the same body of work; it just lives under `specs/` now.

When you create an ADR or a feature spec that establishes a rule an agent must follow, add it to the **Authoritative References** table above.

## Build, Run, Test

```bash
dotnet build src/StarWarsData.slnx
dotnet run --project src/StarWarsData.AppHost      # or: dotnet watch --project src/StarWarsData.AppHost

# Tests (MSTest on Microsoft.Testing.Platform — --project is required)
dotnet test --project src/StarWarsData.Tests --filter "TestCategory=Unit"                              # pre-commit, ~700ms
dotnet test --project src/StarWarsData.Tests --filter "TestCategory=Unit|TestCategory=Integration"    # CI, ~15s (Docker required)
dotnet test --project src/StarWarsData.Tests --filter "TestCategory=Agent"                            # manual; needs STARWARS_OPENAI_KEY + MDB_MCP_CONNECTION_STRING
dotnet test --project src/StarWarsData.Tests --filter "FullyQualifiedName~ClassName.MethodName"       # single class/method
```

Fixtures (`src/StarWarsData.Tests/Infrastructure/`) are lazy-static and wired via `[ClassInitialize]`; assembly-cleanup in `AssemblyHooks.cs`. New tests pick a tier (`Unit/`, `Integration/`, `Agent/`) and tag with `[TestCategory(TestTiers.X)]`. Aspire publish/deploy: [Design-017](specs/017-aspire-publish-deploy-workflow/spec.md). Running AppHost from an agent: [eng/docs/aspire-isolated-mode-for-claude-code.md](eng/docs/aspire-isolated-mode-for-claude-code.md).

## Updating NuGet Packages

`Directory.Build.props` sets `TreatWarningsAsErrors=true`, so any NuGetAudit hit (NU1902/NU1903) on a transitive package hard-fails `dotnet restore` for the whole solution — this has happened before and looks like a mysterious total restore failure rather than "package X is outdated." When bumping packages, in order:

1. Bump direct `PackageReference` versions per-project (no central package management — each `.csproj` under `src/` pins its own).
2. **AGUI alignment**: `Microsoft.Agents.AI`, `.Workflows`, `.OpenAI`, `.AGUI` (Frontend client), `.Hosting.AGUI.AspNetCore` (ApiService hosting) must all move together to the same release — see the version-history comments in `StarWarsData.Frontend.csproj` and `StarWarsData.ApiService.csproj`. Mismatched versions break the AGUI wire protocol on the second turn of a chat.
3. **Re-validate every manual override/pin comment** (`grep -n "Direct override" src/**/*.csproj`) against the live advisory feed (`api.nuget.org/v3/vulnerabilities/index.json`, gzip-encoded) instead of assuming last time's fix still applies or blindly jumping to latest-major — a pin may now be redundant (natural transitive resolution already clears the advisory) or a "latest" version may be a breaking major that isn't source/binary compatible with the package that transitively requires it (e.g. `Microsoft.OpenApi` 3.x breaks `Microsoft.AspNetCore.OpenApi`'s source generator; don't assume `MessagePack` 3.x is safe with the current `StreamJsonRpc` either).
4. **MudBlazor MCP server version**: the `mudblazor` MCP entries in `.mcp.json` and `.vscode/mcp.json` hardcode `--version X.Y.Z` pointing at a local `MudMCP` checkout — bump both to match the new `MudBlazor` package version whenever it changes, or the MCP's component docs/examples drift from what's actually installed.
5. Confirm clean: `dotnet restore src/StarWarsData.slnx --force` (zero warnings — a `NU1902`/`NU1903` warning here means step 3 isn't done), full `dotnet build`, and the `Unit` test tier.

## Architecture (Index)

Five runtime components under `src/`:

- **AppHost** — Aspire orchestrator; defines ETL HTTP commands (1a–9) on the **admin** resource; manages the MongoDB MCP sidecar.
- **ApiService** — ASP.NET Core API; hosts the AI agent + AGUI streaming at `/kernel/stream`. Feature folders under `Features/`.
- **Admin** — Blazor Interactive Server (MudBlazor); ETL pipeline UI + Hangfire dashboard.
- **Frontend** — Blazor Interactive Server (MudBlazor); Keycloak OIDC.
- **MongoDbMigrations** — run-once `mongosh migrate.js` container; idempotent via the `migrations` collection. Scripts in `src/StarWarsData.MongoDbMigrations/`.

Shared libraries: **Models**, **Services** (feature-organised: `AI/Agents`, `AI/Toolkits`, `KnowledgeGraph`, `CharacterTimelines`, `GalaxyMap`, `Search`, `Chat`, `Pages`, `RPG`, `Timeline`, `User`, …), **ServiceDefaults**.

**MongoDB**: single DB (`Settings.DatabaseName`) with namespaced collections `raw.*`, `timeline.*`, `kg.*`, `search.*`, `genai.*`, `chat.*`, `territory.*`, `galaxy.*`, `admin.*`, `hangfire.*`. Connection assembled from AppHost parameters; MCP usage via host `MDB_MCP_CONNECTION_STRING`. Opt-in Aspire-managed dev Mongo via `Parameters:use-local-mongo=true` ([ADR-010](eng/adr/010-aspire-managed-dev-mongo.md)). See Principle II.

**ETL phases** (triggered via admin endpoints / Aspire HTTP commands):

1. Download Wookieepedia pages → `raw.*`
2. MongoDB views per infobox template type
3. Categorised timeline events
4. Indexes + embeddings + vector indexes
5. AI-generated character timelines
6. Deterministic infobox KG (`InfoboxGraphService`, per-type node builders → `kg.*`). LLM enrichment is the separate **Holocron** pass — [Design-018](specs/018-kg-enrichments-architecture/spec.md) / [Design-020](specs/020-holocron-async-pipeline/spec.md).
7. Inferred territory control (`territory.*`, `galaxy.*`).

> Legacy OpenAI Batch relationship-extraction path (`RelationshipGraphBuilderService`, `/graph-builder`) removed 2026-05-18.

**Hangfire recurring jobs**: daily wiki sync (03:00 UTC), KG rebuild (04:00), article chunking (05:00), OpenAI spend (04:30), Holocron (06:00, gated by `HolocronEnabled`); weekly Ask suggestions (Sun 03:00).

## Conventions

- C# preview features + nullable enabled across all projects.
- Feature-based folder layout (`Features/<Name>/`), not layer-based.
- Configuration: `SettingsOptions` from `appsettings.json` section `"Settings"`. Collection names in the `Collections` static class in `Settings.cs`.
- AI stack: `Microsoft.Extensions.AI` + `Microsoft.Agents.AI` + OpenAI SDK. **No Semantic Kernel.**
- Agent classes at `Services/AI/Agents/<Agent>/`; agent-scoped toolkits at `…/<Agent>/Tools/`; cross-agent toolkits at `Services/AI/Toolkits/`. Tool name constants in `ToolNames.cs`.
- Env vars: `STARWARS_OPENAI_KEY` (OpenAI), `MDB_MCP_CONNECTION_STRING` (MongoDB MCP).

## MCP Servers

Use these instead of guessing:

- **Aspire** (`mcp__aspire__*`) — running-resource logs/traces/commands + `search_docs`/`get_doc` for Aspire docs (check first; don't guess at APIs).
- **MongoDB** (`mcp__MongoDB__*`) — connects via host `MDB_MCP_CONNECTION_STRING`. Default DB `starwars-dev`. Never write to `starwars-prod`.
- **Chrome DevTools** (`mcp__chrome-devtools__*`) — **required** for UI validation per Principle IV.
- **MudBlazor** (`mcp__mudblazor__*`) — component docs, parameters, examples.
- **Playwright** (`mcp__playwright__*`) — browser automation.
- **MediaWiki** (`mcp__mediawiki-mcp-server__*`) — Wookieepedia search/fetch.
- **OpenAI Developer Docs** (`mcp__openaiDeveloperDocs__*`) — OpenAI API docs and specs.

## Skills (`/skill-name`)

Domain skills (installed via `gh skill install`; update with `gh skill update --all`):

- `/dotnet-best-practices`, `/microsoft-agent-framework`, `/claude-d3js-skill`, `/likec4-dsl`
- `/mongodb-natural-language-querying`, `/mongodb-query-optimizer`, `/mongodb-schema-design`, `/mongodb-search-and-ai`

Spec-Kit skills: `/speckit-constitution`, `/speckit-specify`, `/speckit-clarify`, `/speckit-plan`, `/speckit-tasks`, `/speckit-checklist`, `/speckit-analyze`, `/speckit-implement`, `/speckit-taskstoissues`, plus git helpers (`/speckit-git-feature`, `/speckit-git-commit`, `/speckit-git-initialize`, `/speckit-git-remote`, `/speckit-git-validate`).

<!-- SPECKIT START -->
For additional context about technologies to be used, project structure,
shell commands, and other important information, read the current plan:
[specs/042-family-tree-component/plan.md](specs/042-family-tree-component/plan.md)
<!-- SPECKIT END -->
