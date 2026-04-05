using Projects;

var builder = DistributedApplication.CreateBuilder(args);
var openApiKey = Environment.GetEnvironmentVariable("STARWARS_OPENAI_KEY") ?? "default-key";
var openApi = builder.AddParameter(name: "openapi", value: openApiKey, secret: true);

// External self-hosted Mongo server: parameterize credentials & host for secret management
var mongoUser = builder.AddParameter("mongo-user", value: "admin");
var mongoPassword = builder.AddParameter("mongo-password", value: "password", secret: true);
var mongoHost = builder.AddParameter("mongo-host", value: "192.168.1.102");
var mongoPort = builder.AddParameter("mongo-port", value: "27018");

var apiService = builder
    .AddProject<StarWarsData_ApiService>("apiservice")
    .WithExternalHttpEndpoints()
    .WithEnvironment("Settings__OpenAiKey", openApi)
    .WithEnvironment("Settings__HangfireEnabled", "true");

var mongo = builder.AddConnectionString("mongodb", ReferenceExpression.Create($"mongodb://{mongoUser}:{mongoPassword}@{mongoHost}:{mongoPort}/?authSource=admin&directConnection=true"));

apiService.WithReference(mongo).WaitFor(mongo);

// MongoDB MCP server as a sidecar container (HTTP transport)
var mongoMcp = builder
    .AddContainer("mongodb-mcp", "mongodb/mongodb-mcp-server", "latest")
    .WithEnvironment("MDB_MCP_CONNECTION_STRING", ReferenceExpression.Create($"mongodb://{mongoUser}:{mongoPassword}@{mongoHost}:{mongoPort}/?authSource=admin&directConnection=true"))
    .WithEnvironment("MDB_MCP_READ_ONLY", "true")
    .WithArgs("--transport", "http", "--httpHost", "0.0.0.0", "--httpPort", "3000")
    .WithHttpEndpoint(targetPort: 3000, name: "mcp");

apiService.WithEnvironment("MCP_MONGODB_URL", mongoMcp.GetEndpoint("mcp")).WaitFor(mongoMcp);

var admin = builder
    .AddProject<StarWarsData_Admin>("admin")
    .WithExternalHttpEndpoints()
    .WithEnvironment("Settings__OpenAiKey", openApi)
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
        path: "/api/admin/mongo/create-categorized-timeline-events",
        displayName: "3a. Build Timeline Events (legacy)",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description = "LEGACY: Text-regex parser over raw.pages infobox links. Superseded by the KG-backed rebuild (3a-KG) — kept for regression comparison only.",
            IconName = "Timeline",
            IsHighlighted = false,
        }
    )
    .WithHttpCommand(
        path: "/api/admin/mongo/create-timeline-events-from-kg",
        displayName: "3a-KG. Build Timeline Events (from KG)",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description = "Rebuilds timeline.* collections from kg.nodes (galactic facets only) joined with raw.pages for info-panel properties. Requires the knowledge graph to be built.",
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
            Description = "Runs ALL index creation in sequence: pages → chunks → vector search → KG graph. Safe to re-run.",
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
        displayName: "5a. Build Infobox Graph",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description = "Builds deterministic knowledge graph (kg.nodes + kg.edges) from infobox data. No LLM needed. Requires Phase 1.",
            IconName = "AccountTree",
            IsHighlighted = false,
        }
    )
    .WithHttpCommand(
        path: "/api/admin/mongo/ensure-graph-indexes",
        displayName: "5b. Ensure Graph Indexes",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description = "Creates MongoDB indexes on kg.nodes and kg.edges for query performance.",
            IconName = "DatabaseSearch",
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
    // ── Phase 7: LLM Relationship Graph (OpenAI Batch API, optional) ──
    .WithHttpCommand(
        path: "/api/admin/mongo/submit-graph-batch",
        displayName: "7a. Submit LLM Graph Batch",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description = "Submits a batch to OpenAI Batch API for LLM relationship extraction. Drip-feeds to respect token quota.",
            IconName = "CloudUpload",
            IsHighlighted = false,
        }
    )
    .WithHttpCommand(
        path: "/api/admin/mongo/check-graph-batches",
        displayName: "7b. Check Graph Batches",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description = "Checks status of in-flight OpenAI batches and processes completed results. Runs every 5 minutes.",
            IconName = "ArrowSync",
            IsHighlighted = false,
        }
    )
    .WithHttpCommand(
        path: "/api/admin/mongo/cleanup-graph-batches",
        displayName: "7c. Cleanup Failed Batches",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description = "Releases orphaned pages from failed/stale batches so they can be resubmitted.",
            IconName = "BroomAll",
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
    );

var frontend = builder
    .AddProject<StarWarsData_Frontend>("frontend")
    .WithExternalHttpEndpoints()
    .WithEnvironment("services__keycloak__https__0", "https://auth.magaoidh.pro")
    .WithReference(apiService)
    .WaitFor(apiService);

#pragma warning disable ASPIREPIPELINES003
var registry = builder.AddContainerRegistry("ghcr", "ghcr.io", "pjmagee");
var imageTag = Environment.GetEnvironmentVariable("CONTAINER_IMAGE_TAG") ?? "latest";
apiService.WithContainerRegistry(registry).WithRemoteImageTag(imageTag);
admin.WithContainerRegistry(registry).WithRemoteImageTag(imageTag);
frontend.WithContainerRegistry(registry).WithRemoteImageTag(imageTag);
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
            service.Restart = "unless-stopped";
            service.Labels ??= [];

            service.Labels["net.unraid.docker.managed"] = "composeman";

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
                    service.Ports = ["${DASHBOARD_HOST_PORT:-18888}:18888"];
                    service.Labels["net.unraid.docker.icon"] = "https://raw.githubusercontent.com/pjmagee/starwars-data/main/.github/icons/dashboard.png";
                    service.Labels["net.unraid.docker.webui"] = "http://[IP]:[PORT:18888]";
                    break;
            }
        }
    });

builder.Build().Run();
