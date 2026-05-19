using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Projects;

var builder = DistributedApplication.CreateBuilder(args);

// AppHost is a developer-only tool — load user-secrets in every environment (not just Development)
// so `aspire do prepare-starwars -e Production` can resolve secret parameters from the dev secret store.
builder.Configuration.AddUserSecrets(typeof(Program).Assembly, optional: true);

// All secret/env-varying values resolve from (in order):
//   1. env vars `Parameters__<name>` (double underscore, dashes → underscores)
//   2. user secrets / appsettings under `Parameters:<name>`
//   3. Aspire dashboard prompt at run, or `aspire publish`/`prepare`/`deploy` prompt
// Set locally with: dotnet user-secrets set "Parameters:<name>" <value> --project src/StarWarsData.AppHost
var openApi = builder.AddParameter("openapi", secret: true);

// Org-scoped OpenAI Admin API key (sk-admin-…) for the Admin app's nightly billing sync.
// Read-only billing scope — never goes to apiservice or frontend. Resolves from user-secrets:
//   dotnet user-secrets set "Parameters:openai-admin-key" sk-admin-... --project src/StarWarsData.AppHost
// If unset, Aspire prompts at AppHost startup. The Admin sync silently no-ops when the value
// is empty, so leaving it blank is safe in dev environments without a billing key.
var openApiAdmin = builder.AddParameter("openai-admin-key", secret: true);
var mongoUser = builder.AddParameter("mongo-user");
var mongoPassword = builder.AddParameter("mongo-password", secret: true);
var mongoHost = builder.AddParameter("mongo-host");
var mongoPort = builder.AddParameter("mongo-port");

// Resolves from appsettings.{Environment}.json: starwars-dev (Development) / starwars-prod (Production).
var starwarsDb = builder.AddParameter("starwars-db");

// Keycloak service-account credentials for GDPR user deletion.
// Default is empty — the real value is injected at deploy time via the host's
// KEYCLOAK_ADMIN_SECRET env var (see ConfigureComposeFile below).
var keycloakAdminSecret = builder.AddParameter("keycloak-admin-secret", value: "", secret: true);

var apiService = builder
    .AddProject<StarWarsData_ApiService>("apiservice")
    .WithExternalHttpEndpoints()
    .WithEnvironment("Settings__OpenAiKey", openApi)
    .WithEnvironment("Settings__DatabaseName", starwarsDb)
    .WithEnvironment("Settings__HangfireEnabled", "true")
    // Holocron is a billed LLM pass — default ON. The literal here is overridden
    // in ConfigureComposeFile to ${HOLOCRON_ENABLED:-true} so prod can still kill
    // it via the hand-maintained .env (HOLOCRON_ENABLED=false) without a redeploy.
    .WithEnvironment("Settings__HolocronEnabled", "true")
    .WithEnvironment("Settings__KeycloakAdminClientSecret", keycloakAdminSecret);

var connString = ReferenceExpression.Create($"mongodb://{mongoUser}:{mongoPassword}@{mongoHost}:{mongoPort}/?authSource=admin&directConnection=true");
var mongo = builder.AddConnectionString("mongodb", connString);

// MongoDB migrations: runs mongosh migrate.js inside mongo:latest, then exits.
// Tracked in the `migrations` collection — re-runs are safe (idempotent).
// Built from src/StarWarsData.MongoDbMigrations/Dockerfile — scripts baked into the image.
var mongoDbMigrations = builder
    .AddDockerfile("mongodb-migrations", "../StarWarsData.MongoDbMigrations")
    .WithEnvironment("MDB_MCP_CONNECTION_STRING", connString)
    .WithEnvironment("STARWARS_DB", starwarsDb);

// Developer-onboarding snapshot restore (Design-038). Development-only,
// run-once: downloads the shared starwars-prod snapshot from OneDrive and
// restores it into the dev database so a fresh clone has full data (incl.
// embeddings) without re-running ETL or spending OpenAI credit. Idempotent —
// no-ops if SNAPSHOT_URL is unset or the dev DB is already populated. NEVER
// added in Production (gated below) so it can't enter the published compose,
// and restore.sh hard-refuses any DB whose name contains "prod".
if (builder.Environment.IsDevelopment() && builder.ExecutionContext.IsRunMode)
{
    // Direct-download URL of the .gz on the shared OneDrive folder. Empty by
    // default (no-op) — set with:
    //   dotnet user-secrets set "Parameters:snapshot-url" "<url>" --project src/StarWarsData.AppHost
    var snapshotUrl = builder.AddParameter("snapshot-url", value: "", secret: true);

    var snapshotRestore = builder
        .AddDockerfile("snapshot-restore", "../StarWarsData.SnapshotRestore")
        .WithEnvironment("MDB_CONNECTION_STRING", connString)
        .WithEnvironment("TARGET_DB", starwarsDb)
        .WithEnvironment("SNAPSHOT_URL", snapshotUrl)
        .WaitFor(mongo);

    // Restore drops & recreates collections — it MUST finish before migrations
    // apply schema/indexes on top, or --drop would wipe migrated state.
    mongoDbMigrations.WaitForCompletion(snapshotRestore);
}

apiService.WithReference(mongo).WaitFor(mongo);

// MongoDB MCP server as a sidecar container (HTTP transport)
var mongoMcp = builder
    .AddContainer("mongodb-mcp", "mongodb/mongodb-mcp-server", "latest")
    .WithEnvironment("MDB_MCP_CONNECTION_STRING", connString)
    .WithEnvironment("MDB_MCP_READ_ONLY", "true")
    .WithArgs("--transport", "http", "--httpHost", "0.0.0.0", "--httpPort", "3000")
    .WithHttpEndpoint(targetPort: 3000, name: "mcp");

apiService.WithEnvironment("MCP_MONGODB_URL", mongoMcp.GetEndpoint("mcp")).WaitFor(mongoMcp);

var admin = builder
    .AddProject<StarWarsData_Admin>("admin")
    .WithExternalHttpEndpoints()
    .WithEnvironment("Settings__OpenAiKey", openApi)
    .WithEnvironment("Settings__OpenAiAdminKey", openApiAdmin)
    .WithEnvironment("Settings__DatabaseName", starwarsDb)
    .WithEnvironment("Settings__HangfireEnabled", "true")
    .WithReference(mongo)
    .WaitFor(mongo)
    .WithReference(apiService)
    // ── Phase 1: Download raw data ──
    .WithHttpCommand(
        path: "/api/admin/download/pages",
        displayName: "1a. Download Pages",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description = "Downloads raw wiki pages into raw.pages collection.",
            IconName = "ArrowDownload",
            IsHighlighted = false,
        }
    )
    .WithHttpCommand(
        path: "/api/admin/download/pages/incremental",
        displayName: "1b. Incremental Sync",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description = "Re-downloads only pages changed since last sync. Runs daily at 03:00 UTC automatically.",
            IconName = "ArrowSync",
            IsHighlighted = false,
        }
    )
    // ── Phase 2: Template views ──
    .WithHttpCommand(
        path: "/api/admin/mongo/create-template-views",
        displayName: "2. Create Template Views",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description = "Creates MongoDB views per infobox template type (Character, Planet, etc.). Requires Phase 1.",
            IconName = "TableMultiple",
            IsHighlighted = false,
        }
    )
    // ── Phase 3: Timeline events + indexes ──
    .WithHttpCommand(
        path: "/api/admin/mongo/create-timeline-events-from-kg",
        displayName: "3a. Build Timeline Events (from KG)",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description =
                "Rebuilds timeline.* collections from kg.nodes — both galactic (BBY/ABY) and real-world (CE publication) facets — joined with raw.pages for info-panel properties. Requires the knowledge graph to be built.",
            IconName = "Timeline",
            IsHighlighted = true,
        }
    )
    .WithHttpCommand(
        path: "/api/admin/mongo/ensure-indexes",
        displayName: "3b. Create Indexes",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description = "Creates indexes on Pages and timeline event collections for query performance.",
            IconName = "DatabaseSearch",
            IsHighlighted = false,
        }
    )
    // ── All Indexes (convenience: runs all index steps in sequence) ──
    .WithHttpCommand(
        path: "/api/admin/mongo/ensure-all-indexes",
        displayName: "Ensure All Indexes",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description = "Runs ALL index creation in sequence: pages → chunks → vector search. Safe to re-run.",
            IconName = "DatabaseSearch",
            IsHighlighted = true,
        }
    )
    // ── Phase 4: Article chunks + embeddings ──
    .WithHttpCommand(
        path: "/api/admin/mongo/create-embeddings",
        displayName: "4a. Run Article Chunking",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description = "Chunks articles and generates OpenAI embeddings. Requires OpenAI key and Phase 1.",
            IconName = "Sparkle",
            IsHighlighted = false,
        }
    )
    .WithHttpCommand(
        path: "/api/admin/mongo/ensure-chunk-indexes",
        displayName: "4b. Ensure Chunk Indexes",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description = "Creates indexes on article chunk collections.",
            IconName = "DatabaseSearch",
            IsHighlighted = false,
        }
    )
    .WithHttpCommand(
        path: "/api/admin/mongo/create-index-embeddings",
        displayName: "4c. Create Vector Indexes",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description = "Creates MongoDB Atlas vector search indexes on embeddings. Run after 4a.",
            IconName = "DatabaseSearch",
            IsHighlighted = false,
        }
    )
    // ── Phase 5: Knowledge Graph (deterministic) ──
    .WithHttpCommand(
        path: "/api/admin/mongo/build-infobox-graph",
        displayName: "5. Build Infobox Graph",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description = "Builds deterministic knowledge graph (kg.nodes + kg.edges) from infobox data. No LLM needed. Requires Phase 1.",
            IconName = "AccountTree",
            IsHighlighted = false,
        }
    )
    // ── Phase 6: AI Character Timelines ──
    .WithHttpCommand(
        path: "/api/admin/mongo/create-character-timelines",
        displayName: "6. Build Character Timelines",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description = "Uses AI to generate rich timeline events for each character. Requires Phase 1 and OpenAI key.",
            IconName = "PersonTimeline",
            IsHighlighted = false,
        }
    )
    // ── Phase 8: Unified Galaxy Map ──
    .WithHttpCommand(
        path: "/api/admin/mongo/build-galaxy-map",
        displayName: "8. Build Galaxy Map",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description = "Pre-computes galaxy.years with territory control, event heatmap, and trade routes from the knowledge graph. Requires Phase 1 + 5.",
            IconName = "GlobeSearch",
            IsHighlighted = false,
        }
    )
    // ── Phase 9: Ask page suggestions (KG-backed dynamic prompts) ──
    .WithHttpCommand(
        path: "/api/admin/mongo/refresh-ask-suggestions",
        displayName: "9. Refresh Ask Suggestions",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description = "AI agent explores the knowledge graph and generates Ask page example questions. Runs weekly (Sundays 03:00 UTC).",
            IconName = "LightbulbFilament",
            IsHighlighted = false,
        }
    )
    // ── Operational: OpenAI billing sync ──
    .WithHttpCommand(
        path: "/api/admin/openai/sync-spend",
        displayName: "Sync OpenAI Spend",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description =
                "Pulls the last 90 days of OpenAI org spend from /v1/organization/costs and upserts admin.spend_daily (powers the public /costs page). Runs daily 04:30 UTC; trigger here to refresh on demand. Requires Settings.OpenAiAdminKey.",
            IconName = "Money",
            IsHighlighted = false,
        }
    )
    // ── Dev QA: pull fresh raw content from prod (Design-033) ──
    .WithHttpCommand(
        path: "/api/admin/sync/prod-to-dev/recent",
        displayName: "↩ Pull Prod → Dev (raw, last 14d)",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description =
                "Copies raw.pages changed in prod within the last 14 days into the dev "
                + "database via a server-side $merge. Read-only against prod; refuses to run "
                + "if target is starwars-prod. Run Phase 5 → 3a → 4a afterward to rebuild "
                + "dev's derived data.",
            IconName = "DatabaseArrowDown",
            IsHighlighted = false,
        }
    );

var frontend = builder
    .AddProject<StarWarsData_Frontend>("frontend")
    .WithExternalHttpEndpoints()
    .WithEnvironment("services__keycloak__https__0", "https://auth.magaoidh.pro")
    .WithReference(apiService)
    .WaitFor(apiService);

#pragma warning disable ASPIREPIPELINES003
var registry = builder.AddContainerRegistry("ghcr", "ghcr.io", "pjmagee/starwars-data");
var imageTag = Environment.GetEnvironmentVariable("CONTAINER_IMAGE_TAG") ?? "latest";
apiService.WithContainerRegistry(registry).WithRemoteImageTag(imageTag);
admin.WithContainerRegistry(registry).WithRemoteImageTag(imageTag);
frontend.WithContainerRegistry(registry).WithRemoteImageTag(imageTag);
mongoDbMigrations.WithContainerRegistry(registry).WithRemoteImageTag(imageTag);
#pragma warning restore ASPIREPIPELINES003

builder
    .AddDockerComposeEnvironment("starwars")
    .ConfigureEnvFile(static env =>
    {
        env["FRONTEND_HOST_PORT"] = new() { Name = "FRONTEND_HOST_PORT", DefaultValue = "9081" };
        env["APISERVICE_HOST_PORT"] = new() { Name = "APISERVICE_HOST_PORT", DefaultValue = "9080" };
        env["ADMIN_HOST_PORT"] = new() { Name = "ADMIN_HOST_PORT", DefaultValue = "9082" };
        env["DASHBOARD_HOST_PORT"] = new() { Name = "DASHBOARD_HOST_PORT", DefaultValue = "18888" };
    })
    .ConfigureComposeFile(static compose =>
    {
        // Configure all services with restart policy and Unraid labels
        foreach (var (name, service) in compose.Services)
        {
            // Migration container runs once and exits — don't restart it
            service.Restart = name == "mongodb-migrations" ? "no" : "unless-stopped";
            service.Labels ??= [];

            service.Labels["net.unraid.docker.managed"] = "composeman";

            // Keycloak admin secret is injected at deploy time via the host's KEYCLOAK_ADMIN_SECRET env var
            // (rather than via the AppHost parameter system, so it never lands in the published .env).
            if (name is "apiservice")
            {
                service.Environment ??= [];
                service.Environment["Settings__KeycloakAdminClientSecret"] = "${KEYCLOAK_ADMIN_SECRET:-}";
                // Billed LLM pass — on by default; set HOLOCRON_ENABLED=false in the
                // host's prod .env to kill it without a code change/redeploy.
                service.Environment["Settings__HolocronEnabled"] = "${HOLOCRON_ENABLED:-true}";
            }

            switch (name)
            {
                case "apiservice":
                    service.Ports = ["${APISERVICE_HOST_PORT:-9080}:${APISERVICE_PORT}"];
                    service.Labels["net.unraid.docker.icon"] = "https://raw.githubusercontent.com/pjmagee/starwars-data/main/.github/icons/api.png";
                    service.Labels["net.unraid.docker.webui"] = "http://[IP]:[PORT:${APISERVICE_PORT}]/swagger";
                    break;
                case "frontend":
                    service.Ports = ["${FRONTEND_HOST_PORT:-9081}:${FRONTEND_PORT}"];
                    service.Labels["net.unraid.docker.icon"] = "https://raw.githubusercontent.com/pjmagee/starwars-data/main/.github/icons/frontend.png";
                    service.Labels["net.unraid.docker.webui"] = "http://[IP]:[PORT:${FRONTEND_PORT}]";
                    break;
                case "admin":
                    service.Ports = ["${ADMIN_HOST_PORT:-9082}:${ADMIN_PORT}"];
                    service.Labels["net.unraid.docker.icon"] = "https://raw.githubusercontent.com/pjmagee/starwars-data/main/.github/icons/api.png";
                    service.Labels["net.unraid.docker.webui"] = "http://[IP]:[PORT:${ADMIN_PORT}]";
                    break;
                case "starwars-dashboard":
                    // Dashboard is on the internal LAN only — never exposed via Cloudflare Tunnel —
                    // so anonymous access is acceptable and avoids needing OIDC config in the compose env.
                    service.Environment ??= [];
                    service.Environment["ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS"] = "true";
                    service.Labels["net.unraid.docker.icon"] = "https://raw.githubusercontent.com/pjmagee/starwars-data/main/.github/icons/dashboard.png";
                    service.Labels["net.unraid.docker.webui"] = "http://[IP]:[PORT:18888]";
                    break;
            }
        }
    })
    .WithDashboard(dashboard =>
    {
        dashboard.WithHostPort(18888);
    });

builder.Build().Run();
