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
        path: "/api/admin/download/infoboxes",
        displayName: "1a. Download Infoboxes",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description = "Downloads raw infobox data from the Star Wars wiki API into starwars-raw-infoboxes.",
            IconName = "ArrowDownload",
            IsHighlighted = false,
        })
    .WithHttpCommand(
        path: "/api/admin/download/pages",
        displayName: "1b. Download Pages",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description = "Downloads raw wiki pages into starwars-raw-pages.",
            IconName = "ArrowDownload",
            IsHighlighted = false,
        })
    // Phase 2 — Extract & process
    .WithHttpCommand(
        path: "/api/admin/extract/infoboxes",
        displayName: "2a. Extract Infoboxes",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description = "Extracts structured infoboxes from raw pages into starwars-extracted-infoboxes. Requires Phase 1b.",
            IconName = "DocumentBulletList",
            IsHighlighted = false,
        })
    .WithHttpCommand(
        path: "/api/admin/process/infobox-relationships",
        displayName: "2b. Process Relationships",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description = "Builds infobox relationships in starwars-raw-infoboxes. Requires Phase 1a.",
            IconName = "DataTreemap",
            IsHighlighted = false,
        })
    // Phase 3 — Timeline events
    .WithHttpCommand(
        path: "/api/admin/mongo/create-categorized-timeline-events",
        displayName: "3. Build Timeline Events",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description = "Creates categorized timeline events in starwars-timeline-events. Requires Phase 2a.",
            IconName = "Timeline",
            IsHighlighted = false,
        })
    // Phase 4 — Character relationships
    .WithHttpCommand(
        path: "/api/admin/mongo/add-character-relationships",
        displayName: "4. Add Character Relationships",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description = "Builds character relationship graph in starwars-structured. Requires Phase 2b.",
            IconName = "PeopleTeam",
            IsHighlighted = false,
        })
    // Phase 5 — Embeddings (optional)
    .WithHttpCommand(
        path: "/api/admin/mongo/create-embeddings",
        displayName: "5a. Create Embeddings",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description = "Generates OpenAI embeddings. Requires OpenAI key and Phase 2.",
            IconName = "Sparkle",
            IsHighlighted = false,
        })
    .WithHttpCommand(
        path: "/api/admin/mongo/create-index-embeddings",
        displayName: "5b. Create Vector Indexes",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description = "Creates MongoDB vector indexes. Run after 5a.",
            IconName = "DatabaseSearch",
            IsHighlighted = false,
        });

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
        ReferenceExpression.Create($"mongodb://{mongoUser}:{mongoPassword}@{mongoHost}:{mongoPort}/?authSource=admin")
    );

    apiService.WithReference(mongo).WaitFor(mongo);
}

var frontend = builder
    .AddProject<StarWarsData_Frontend>("frontend")
    .WithReference(apiService)
    .WaitFor(apiService)
;

builder.Build().Run();

IResourceBuilder<ConnectionStringResource> GetMongoAtlas()
{
    var mongoConnectionString = Environment.GetEnvironmentVariable("MONGO_CONNECTION_STRING");
    return builder.AddConnectionString(
        name: "mongodb",
        ReferenceExpression.Create($"{mongoConnectionString}")
    );
}
