using Projects;

var builder = DistributedApplication.CreateBuilder(args);
var openApiKey = Environment.GetEnvironmentVariable("OPENAI_KEY") ?? "default-key";
var openApi = builder.AddParameter(name: "openapi", value: openApiKey, secret: true);

var apiService = builder.AddProject<StarWarsData_ApiService>("apiservice")
    .WithExternalHttpEndpoints()
    .WithEnvironment("Settings__OpenAiKey", openApi);

if (builder.ExecutionContext.IsPublishMode)
{
    var mongo = GetMongoAtlas();
    apiService.WithReference(mongo).WaitFor(mongo);
}
else
{
    var mongo = GetMongoLocal();
    apiService.WithReference(mongo).WaitFor(mongo);
}

var frontend = builder.AddProject<StarWarsData_Frontend>("frontend")
    .WithReference(apiService)
    .WaitFor(apiService);

builder.Build().Run();


IResourceBuilder<MongoDBServerResource> GetMongoLocal()
{
    IResourceBuilder<MongoDBServerResource> mongo = builder.AddMongoDB("mongodb")
        .WithMongoExpress()
        .WithLifetime(ContainerLifetime.Persistent)
        .WithDataVolume();

    mongo.AddDatabase("starwars-raw");
    mongo.AddDatabase("starwars-structured");
    return mongo;
}

IResourceBuilder<ConnectionStringResource> GetMongoAtlas()
{
    // MongoDB Atlas
    var mongoConnectionString = Environment.GetEnvironmentVariable("MONGO_CONNECTION_STRING");
    return builder.AddConnectionString(name: "mongodb", ReferenceExpression.Create($"{mongoConnectionString}"));
}