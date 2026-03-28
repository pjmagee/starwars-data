using Aspire.Hosting.Docker.Resources.ComposeNodes;
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
    // Phase 1 — Download raw data
    .WithHttpCommand(
        path: "/api/admin/download/pages",
        displayName: "1. Download Pages",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description = "Downloads raw wiki pages into starwars-raw-pages.",
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
            Description =
                "Re-downloads only pages changed since last sync. Runs daily at 03:00 UTC automatically.",
            IconName = "ArrowSync",
            IsHighlighted = false,
        }
    )
    // Phase 2 — Template views
    .WithHttpCommand(
        path: "/api/admin/mongo/create-template-views",
        displayName: "2. Create Template Views",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description =
                "Creates MongoDB views per infobox template type (Character, Planet, etc.). Requires Phase 1.",
            IconName = "TableMultiple",
            IsHighlighted = false,
        }
    )
    // Phase 3 — Timeline events
    .WithHttpCommand(
        path: "/api/admin/mongo/create-categorized-timeline-events",
        displayName: "3. Build Timeline Events",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description =
                "Creates categorized timeline events in starwars-timeline-events from Pages. Requires Phase 1.",
            IconName = "Timeline",
            IsHighlighted = false,
        }
    )
    // Phase 3b — Indexes
    .WithHttpCommand(
        path: "/api/admin/mongo/ensure-indexes",
        displayName: "3b. Create Indexes",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description =
                "Creates indexes on Pages and timeline event collections for query performance.",
            IconName = "DatabaseSearch",
            IsHighlighted = false,
        }
    )
    // Phase 4 — Embeddings (optional)
    .WithHttpCommand(
        path: "/api/admin/mongo/create-embeddings",
        displayName: "4a. Create Embeddings",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description = "Generates OpenAI embeddings. Requires OpenAI key and Phase 1.",
            IconName = "Sparkle",
            IsHighlighted = false,
        }
    )
    .WithHttpCommand(
        path: "/api/admin/mongo/create-index-embeddings",
        displayName: "4b. Create Vector Indexes",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description = "Creates MongoDB vector indexes. Run after 3a.",
            IconName = "DatabaseSearch",
            IsHighlighted = false,
        }
    )
    // Phase 5 — Character Timelines (AI-generated)
    .WithHttpCommand(
        path: "/api/admin/mongo/create-character-timelines",
        displayName: "5. Build Character Timelines",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description =
                "Uses AI to generate rich timeline events for each character by analyzing all linked pages. Requires Phase 1 and OpenAI key.",
            IconName = "PersonTimeline",
            IsHighlighted = false,
        }
    )
    // Phase 6 — Relationship Graph (OpenAI Batch API)
    .WithHttpCommand(
        path: "/api/admin/mongo/submit-graph-batch",
        displayName: "6a. Submit Graph Batch",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description =
                "Submits one batch of unprocessed pages to the OpenAI Batch API for relationship extraction. Drip-feeds to respect token quota.",
            IconName = "CloudUpload",
            IsHighlighted = false,
        }
    )
    .WithHttpCommand(
        path: "/api/admin/mongo/check-graph-batches",
        displayName: "6b. Check Graph Batches",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description =
                "Checks status of in-flight OpenAI batches and processes completed results. Runs automatically every 5 minutes.",
            IconName = "ArrowSync",
            IsHighlighted = false,
        }
    )
    .WithHttpCommand(
        path: "/api/admin/mongo/cleanup-graph-batches",
        displayName: "6c. Cleanup Failed Batches",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description =
                "Releases orphaned pages from failed/stale batches so they can be resubmitted. Run this if batches are stuck.",
            IconName = "BroomAll",
            IsHighlighted = false,
        }
    )
    .WithHttpCommand(
        path: "/api/admin/mongo/ensure-graph-indexes",
        displayName: "6d. Ensure Graph Indexes",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description =
                "Creates MongoDB indexes on the relationship graph collections for query performance.",
            IconName = "DatabaseSearch",
            IsHighlighted = false,
        }
    );

var mongo = builder.AddConnectionString(
    "mongodb",
    ReferenceExpression.Create(
        $"mongodb://{mongoUser}:{mongoPassword}@{mongoHost}:{mongoPort}/?authSource=admin&directConnection=true"
    )
);

apiService.WithReference(mongo).WaitFor(mongo);

var frontend = builder
    .AddProject<StarWarsData_Frontend>("frontend")
    .WithEnvironment("services__keycloak__https__0", "https://auth.magaoidh.pro")
    .WithReference(apiService)
    .WaitFor(apiService);

builder.AddContainerRegistry("ghcr", "ghcr.io", "pjmagee");

builder
    .AddDockerComposeEnvironment("starwars")
    .ConfigureComposeFile(compose =>
    {
        // Replace default network with external proxynet
        compose.Networks.Clear();
        compose.AddNetwork(new Network { Name = "proxynet", External = true });

        // Keycloak runs externally at auth.magaoidh.pro — remove from compose
        compose.Services.Remove("keycloak");

        // Update frontend to use external Keycloak instead of compose service
        if (compose.Services.TryGetValue("frontend", out var frontend))
        {
            frontend.DependsOn?.Remove("keycloak");
            frontend.Environment["KEYCLOAK_HTTP"] = "https://auth.magaoidh.pro";
            frontend.Environment["services__keycloak__http__0"] = "https://auth.magaoidh.pro";
            frontend.Environment.Remove("KEYCLOAK_MANAGEMENT");
            frontend.Environment.Remove("services__keycloak__management__0");
        }

        // Configure all services with restart policy, proxynet, and Unraid labels
        foreach (var (name, service) in compose.Services)
        {
            service.Restart = "unless-stopped";
            service.Networks = ["proxynet"];
            service.Labels ??= [];

            service.Labels["net.unraid.docker.managed"] = "composeman";

            switch (name)
            {
                case "apiservice":
                    service.Labels["net.unraid.docker.icon"] =
                        "https://raw.githubusercontent.com/pjmagee/starwars-data/main/.github/icons/api.png";
                    service.Labels["net.unraid.docker.webui"] = "http://[IP]:[PORT:8080]/swagger";
                    break;
                case "frontend":
                    service.Labels["net.unraid.docker.icon"] =
                        "https://raw.githubusercontent.com/pjmagee/starwars-data/main/.github/icons/frontend.png";
                    service.Labels["net.unraid.docker.webui"] = "http://[IP]:[PORT:8080]";
                    break;
                case "starwars-dashboard":
                    service.Labels["net.unraid.docker.icon"] =
                        "https://raw.githubusercontent.com/pjmagee/starwars-data/main/.github/icons/dashboard.png";
                    service.Labels["net.unraid.docker.webui"] = "http://[IP]:[PORT:18888]";
                    break;
            }
        }
    });

builder.Build().Run();
