using Projects;

var builder = DistributedApplication.CreateBuilder(args);
var openApiKey = Environment.GetEnvironmentVariable("STARWARS_OPENAI_KEY") ?? "default-key";
var openApi = builder.AddParameter(name: "openapi", value: openApiKey, secret: true);

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
    );

if (builder.ExecutionContext.IsPublishMode)
{
    var mongo = GetMongoAtlas();
    apiService.WithReference(mongo).WaitFor(mongo);
}
else
{
    // External self-hosted Mongo server: parameterize credentials & host for secret management
    var mongoUser = builder.AddParameter("mongo-user", value: "admin");
    var mongoPassword = builder.AddParameter("mongo-password", value: "password", secret: true);
    var mongoHost = builder.AddParameter("mongo-host", value: "192.168.1.102");
    var mongoPort = builder.AddParameter("mongo-port", value: "27017");

    var mongo = builder.AddConnectionString(
        "mongodb",
        ReferenceExpression.Create(
            $"mongodb://{mongoUser}:{mongoPassword}@{mongoHost}:{mongoPort}/?authSource=admin"
        )
    );

    apiService.WithReference(mongo).WaitFor(mongo);
}

var keycloak = builder.AddKeycloak("keycloak").WithRealmImport("./KeycloakConfiguration");

var frontend = builder
    .AddProject<StarWarsData_Frontend>("frontend")
    .WithReference(keycloak)
    .WithReference(apiService)
    .WaitFor(keycloak)
    .WaitFor(apiService);

builder.Build().Run();

IResourceBuilder<ConnectionStringResource> GetMongoAtlas()
{
    var mongoConnectionString = Environment.GetEnvironmentVariable("MONGO_CONNECTION_STRING");
    return builder.AddConnectionString(
        name: "mongodb",
        ReferenceExpression.Create($"{mongoConnectionString}")
    );
}
