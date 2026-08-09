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
    // Holocron is a billed LLM pass — default ON. The literal here is overridden
    // in ConfigureComposeFile to ${HOLOCRON_ENABLED:-true} so prod can still kill
    // it via the hand-maintained .env (HOLOCRON_ENABLED=false) without a redeploy.
    .WithEnvironment("Settings__HolocronEnabled", "true")
    .WithEnvironment("Settings__KeycloakAdminClientSecret", keycloakAdminSecret);

// OPT-IN local Mongo for a fresh clone with no MongoDB server. Default false =
// the external self-hosted server, so production AND existing server-based dev
// workflows are byte-identical to before — nobody on the LAN is affected. When
// true (Development + run mode only, so it can never enter
// `aspire publish`/`prepare`/`deploy`) the AppHost runs Mongo itself: Atlas
// Local (NOT plain mongo — vector/text search is required) with a named volume
// + persistent lifetime so the ~15 GB snapshot restore runs ONCE and survives
// `aspire run` restarts. See ADR-010.
//
// Registered as a first-class parameter like mongo-host etc. — dashboard /
// manifest visible, same resolution chain (env `Parameters__use_local_mongo`
// > user-secrets > appsettings), `value: "false"` default so it is never an
// unresolved prompt in prod/CI (mirrors `keycloak-admin-secret` above). It is
// also checked into appsettings.Development.json under `Parameters:` next to
// mongo-host so the knob is discoverable. A ParameterResource is a deferred
// reference that cannot gate resource creation, so the build-time topology
// branch reads the value from configuration — the pattern the Aspire "External
// parameters" docs explicitly sanction for AppHost-side reads.
builder.AddParameter("use-local-mongo", value: "false");

var useLocalMongo = builder.Environment.IsDevelopment() && builder.ExecutionContext.IsRunMode && builder.Configuration.GetValue("Parameters:use-local-mongo", false);

IResourceBuilder<ContainerResource>? mongoLocal = null;
ReferenceExpression connString;

if (useLocalMongo)
{
    mongoLocal = builder
        .AddContainer("mongodb-local", "mongodb/mongodb-atlas-local", "latest")
        .WithEndpoint(port: 27017, targetPort: 27017, scheme: "tcp", name: "mongo")
        .WithVolume("starwars-dev-mongo", "/data/db")
        .WithLifetime(ContainerLifetime.Persistent);

    var ep = mongoLocal.GetEndpoint("mongo");
    connString = ReferenceExpression.Create($"mongodb://{ep.Property(EndpointProperty.Host)}:{ep.Property(EndpointProperty.Port)}/?directConnection=true");
}
else
{
    connString = ReferenceExpression.Create($"mongodb://{mongoUser}:{mongoPassword}@{mongoHost}:{mongoPort}/?authSource=admin&directConnection=true");
}

var mongo = builder.AddConnectionString("mongodb", connString);

// MongoDB migrations: runs mongosh migrate.js inside mongo:latest, then exits.
// Tracked in the `migrations` collection — re-runs are safe (idempotent).
// Built from src/StarWarsData.MongoDbMigrations/Dockerfile — scripts baked into the image.
var mongoDbMigrations = builder
    .AddDockerfile("mongodb-migrations", "../StarWarsData.MongoDbMigrations")
    .WithEnvironment("MDB_MCP_CONNECTION_STRING", connString)
    .WithEnvironment("STARWARS_DB", starwarsDb);

// Developer-onboarding snapshot restore (Design-038). Gated on `useLocalMongo`
// — it ONLY runs when Aspire owns the local Mongo container. This is critical:
// if it ran against the external shared server it would `mongorestore --drop`
// over a live shared `starwars-dev`. Run-once: downloads the snapshot from
// copyparty, restores into the local container. Idempotent (no-ops once
// populated); restore.sh also hard-refuses any DB whose name contains "prod".
if (useLocalMongo)
{
    // Defaulted so a fresh clone needs ZERO config for data — `aspire run`
    // just restores. The URL is not a secret (copyparty access is the gate),
    // so it's a normal parameter; override only if the host/file changes:
    //   dotnet user-secrets set "Parameters:snapshot-url" "<url>" --project src/StarWarsData.AppHost
    var snapshotUrl = builder.AddParameter("snapshot-url", value: "https://copyparty.magaoidh.pro/swdata/starwars-snapshot-latest.gz");

    var snapshotRestore = builder
        .AddDockerfile("snapshot-restore", "../StarWarsData.SnapshotRestore")
        .WithEnvironment("MDB_CONNECTION_STRING", connString)
        .WithEnvironment("TARGET_DB", starwarsDb)
        .WithEnvironment("SNAPSHOT_URL", snapshotUrl)
        // Wait for the actual Mongo container (the AddConnectionString resource
        // has no lifecycle to wait on). mongoLocal is non-null here — this
        // block's guard is exactly `useLocalMongo`.
        .WaitFor(mongoLocal!);

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
        commandOptions: Etl("Downloads raw wiki pages into raw.pages collection.", "ArrowDownload")
    )
    .WithHttpCommand(
        path: "/api/admin/download/pages/incremental",
        displayName: "1b. Incremental Sync",
        commandOptions: Etl("Re-downloads only pages changed since last sync. Runs daily at 03:00 UTC automatically.", "ArrowSync")
    )
    // ── Phase 2: Template views ──
    .WithHttpCommand(
        path: "/api/admin/mongo/create-template-views",
        displayName: "2. Create Template Views",
        commandOptions: Etl("Creates MongoDB views per infobox template type (Character, Planet, etc.). Requires Phase 1.", "TableMultiple")
    )
    // ── Phase 3: Timeline events + indexes ──
    .WithHttpCommand(
        path: "/api/admin/mongo/create-timeline-events-from-kg",
        displayName: "3a. Build Timeline Events (from KG)",
        commandOptions: Etl(
            "Rebuilds timeline.* collections from kg.nodes — both galactic (BBY/ABY) and real-world (CE publication) facets — joined with raw.pages for info-panel properties. Requires the knowledge graph to be built.",
            "Timeline",
            highlighted: true
        )
    )
    .WithHttpCommand(
        path: "/api/admin/mongo/ensure-indexes",
        displayName: "3b. Create Indexes",
        commandOptions: Etl("Creates indexes on Pages and timeline event collections for query performance.", "DatabaseSearch")
    )
    // ── All Indexes (convenience: runs all index steps in sequence) ──
    .WithHttpCommand(
        path: "/api/admin/mongo/ensure-all-indexes",
        displayName: "Ensure All Indexes",
        commandOptions: Etl("Runs ALL index creation in sequence: pages → chunks → vector search. Safe to re-run.", "DatabaseSearch", highlighted: true)
    )
    // ── Phase 4: Article chunks + embeddings ──
    .WithHttpCommand(
        path: "/api/admin/mongo/create-embeddings",
        displayName: "4a. Run Article Chunking",
        commandOptions: Etl("Chunks articles and generates OpenAI embeddings. Requires OpenAI key and Phase 1.", "Sparkle")
    )
    .WithHttpCommand(
        path: "/api/admin/mongo/ensure-chunk-indexes",
        displayName: "4b. Ensure Chunk Indexes",
        commandOptions: Etl("Creates indexes on article chunk collections.", "DatabaseSearch")
    )
    .WithHttpCommand(
        path: "/api/admin/mongo/create-index-embeddings",
        displayName: "4c. Create Vector Indexes",
        commandOptions: Etl("Creates MongoDB Atlas vector search indexes on embeddings. Run after 4a.", "DatabaseSearch")
    )
    // ── Phase 5: Knowledge Graph (deterministic) ──
    .WithHttpCommand(
        path: "/api/admin/mongo/build-infobox-graph",
        displayName: "5. Build Infobox Graph",
        commandOptions: Etl("Builds deterministic knowledge graph (kg.nodes + kg.edges) from infobox data. No LLM needed. Requires Phase 1.", "AccountTree")
    )
    // ── Phase 6: AI Character Timelines ──
    .WithHttpCommand(
        path: "/api/admin/mongo/create-character-timelines",
        displayName: "6. Build Character Timelines",
        commandOptions: Etl("Uses AI to generate rich timeline events for each character. Requires Phase 1 and OpenAI key.", "PersonTimeline")
    )
    // ── Phase 8: Unified Galaxy Map ──
    .WithHttpCommand(
        path: "/api/admin/mongo/build-galaxy-map",
        displayName: "8. Build Galaxy Map",
        commandOptions: Etl("Pre-computes galaxy.years with territory control, event heatmap, and trade routes from the knowledge graph. Requires Phase 1 + 5.", "GlobeSearch")
    )
    // ── Phase 9: Ask page suggestions (KG-backed dynamic prompts) ──
    .WithHttpCommand(
        path: "/api/admin/mongo/refresh-ask-suggestions",
        displayName: "9. Refresh Ask Suggestions",
        commandOptions: Etl("AI agent explores the knowledge graph and generates Ask page example questions. Runs weekly (Sundays 03:00 UTC).", "LightbulbFilament")
    )
    // ── Operational: OpenAI billing sync ──
    .WithHttpCommand(
        path: "/api/admin/openai/sync-spend",
        displayName: "Sync OpenAI Spend",
        commandOptions: Etl(
            "Pulls the last 90 days of OpenAI org spend from /v1/organization/costs and upserts admin.spend_daily (powers the public /costs page). Runs daily 04:30 UTC; trigger here to refresh on demand. Requires Settings.OpenAiAdminKey.",
            "Money"
        )
    )
    // ── Dev QA: pull fresh raw content from prod (Design-033) ──
    .WithHttpCommand(
        path: "/api/admin/sync/prod-to-dev/recent",
        displayName: "↩ Pull Prod → Dev (raw, last 14d)",
        commandOptions: Etl(
            "Copies raw.pages changed in prod within the last 14 days into the dev "
                + "database via a server-side $merge. Read-only against prod; refuses to run "
                + "if target is starwars-prod. Run Phase 5 → 3a → 4a afterward to rebuild "
                + "dev's derived data.",
            "DatabaseArrowDown"
        )
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

            switch (name)
            {
                case "apiservice":
                    // Keycloak admin secret is injected at deploy time via the host's KEYCLOAK_ADMIN_SECRET env var
                    // (rather than via the AppHost parameter system, so it never lands in the published .env).
                    service.Environment ??= [];
                    service.Environment["Settings__KeycloakAdminClientSecret"] = "${KEYCLOAK_ADMIN_SECRET:-}";
                    // Billed LLM pass — on by default; set HOLOCRON_ENABLED=false in the
                    // host's prod .env to kill it without a code change/redeploy.
                    service.Environment["Settings__HolocronEnabled"] = "${HOLOCRON_ENABLED:-true}";
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

// When Aspire owns the Mongo container (local dev), every Mongo consumer must
// wait for it to be running — the `mongodb` AddConnectionString resource has no
// lifecycle of its own to gate on. No-op in Production (mongoLocal is null;
// the external server is assumed up).
if (mongoLocal is not null)
{
    apiService.WaitFor(mongoLocal);
    admin.WaitFor(mongoLocal);
    mongoMcp.WaitFor(mongoLocal);
    mongoDbMigrations.WaitFor(mongoLocal);
}

builder.Build().Run();

static HttpCommandOptions Etl(string description, string icon, bool highlighted = false) => new()
{
    Description = description,
    IconName = icon,
    IsHighlighted = highlighted,
};
