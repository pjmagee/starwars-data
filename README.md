# Star Wars Data

A .NET Blazor application — a knowledge graph and AI assistant over Wookieepedia data. Aspire orchestrates the services locally and produces the deploy artifact, but Aspire is the dev/build harness, not the app.

## What's inside

- **Ask AI** — A streaming chat agent with chart, graph, timeline, table, infobox, and markdown render tools. Every value is sourced from a tool call; nothing is fabricated.
- **Semantic search** — Vector search over Wookieepedia article passages, available standalone and inside the agent.
- **Knowledge graph** — Entities and relationships extracted deterministically from infobox data, with temporal facets across six semantic dimensions and two calendar systems (galactic BBY/ABY + real-world CE).
- **Holocron** — An async LLM pass that proposes annotations and refines temporal bounds on top of the deterministic graph, gated and checkpoint-resumable.
- **Galaxy map** — Drill from regions to planets, plus an animated timeline mode with territory control and faction overlays.
- **Graph explorer, timeline browser, data tables, AI-generated character timelines.**

## How it's wired

The authoritative description lives in [`src/StarWarsData.AppHost/Program.cs`](src/StarWarsData.AppHost/Program.cs) — the AppHost composes:

| Resource | Role |
| --- | --- |
| `frontend` | Blazor Interactive Server UI (MudBlazor). Public; authenticates via Keycloak OIDC. |
| `apiservice` | ASP.NET Core API. Hosts the AI agent (Microsoft.Extensions.AI + Microsoft.Agents.AI, streamed via AGUI). Internal-only — user identity forwarded from the frontend. |
| `admin` | Blazor admin app. ETL triggers (exposed as Aspire HTTP commands) and Hangfire recurring jobs. |
| `mongodb` | Either an external MongoDB server (default) or an Aspire-managed `mongodb-atlas-local` container (opt-in via `use-local-mongo`, see [ADR-010](eng/adr/010-aspire-managed-dev-mongo.md)). |
| `mongodb-mcp` | MongoDB MCP server sidecar — the agent issues read-only queries through it. |
| `mongodb-migrations` | Run-once container that applies schema/index migrations via mongosh. |
| `snapshot-restore` | Dev-only run-once resource that restores a prod snapshot into the local Mongo container. |

## Run locally

See **[ONBOARDING.md](ONBOARDING.md)**.

## How we deploy

A push to `main` triggers [`.github/workflows/deploy.yml`](.github/workflows/deploy.yml):

1. GitVersion → build → unit tests.
2. `aspire do push` builds container images for `apiservice`, `admin`, `frontend`, and `mongodb-migrations`, pushing to `ghcr.io/pjmagee/starwars-data` with both a semver tag and `latest`.
3. `aspire publish -e Production` emits a `docker-compose.yaml` and an **unfilled** `.env.Production` template. CI never sees secrets — the artifact is grep-scanned for OpenAI keys and Mongo passwords before upload, and the build fails if anything leaks.
4. The artifact is uploaded to the GitHub Release; the deploy host materialises real values into `.env.Production` from its own secret store and runs `docker compose --env-file .env.Production up -d`.

Full contract: [specs/017-aspire-publish-deploy-workflow/spec.md](specs/017-aspire-publish-deploy-workflow/spec.md).

## More

Anything authoritative — projects, conventions, ETL phases, MongoDB collection namespaces, ADRs, design docs — lives in [`CLAUDE.md`](CLAUDE.md) and [`eng/`](eng/). This README intentionally stops where the drift-prone facts start.

## Support

Built and maintained as a solo effort. Significant time and cost go into running AI models — Claude (Anthropic) for development, OpenAI for the in-app assistant and embeddings.

<a href="https://www.buymeacoffee.com/pjmagee"><img src="https://img.buymeacoffee.com/button-api/?text=Buy me a coffee&emoji=&slug=pjmagee&button_colour=FFDD00&font_colour=000000&font_family=Poppins&outline_colour=000000&coffee_colour=ffffff" alt="Buy me a coffee" /></a>
