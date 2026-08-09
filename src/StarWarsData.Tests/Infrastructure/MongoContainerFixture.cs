using MongoDB.Driver;
using StarWarsData.Models.Entities;
using Testcontainers.MongoDb;

namespace StarWarsData.Tests.Infrastructure;

/// <summary>
/// Single lazy-initialized MongoDB Testcontainer shared across Integration tests
/// that need an isolated database. Each consumer picks its own DB name via
/// <see cref="GetDatabase"/> so data stays isolated without booting N containers.
/// Independent of <see cref="ApiFixture"/> so these tests don't carry seed overhead.
/// </summary>
public static class MongoContainerFixture
{
    /// <summary>Default DB name used by <see cref="StarWarsData.Services.MongoCheckpointStore"/> tests.</summary>
    public const string CheckpointDbName = "test-checkpoints";

    private static readonly SemaphoreSlim Lock = new(1, 1);
    private static MongoDbContainer? _container;
    private static IMongoClient? _client;

    public static IMongoClient Client =>
        _client ?? throw new InvalidOperationException("MongoContainerFixture not initialized — call EnsureInitializedAsync from [ClassInitialize]");

    public static async Task EnsureInitializedAsync()
    {
        if (_container is not null)
            return;

        await Lock.WaitAsync();
        try
        {
            if (_container is not null)
                return;

            var container = new MongoDbBuilder("mongo:8").Build();
            await container.StartAsync();
            _client = new MongoClient(container.GetConnectionString());
            _container = container;
        }
        finally
        {
            Lock.Release();
        }
    }

    public static IMongoDatabase GetDatabase(string name) => Client.GetDatabase(name);

    /// <summary>
    /// Minimal <see cref="GraphNode"/> seed used by CitationResolver / EventsAtLocation tests.
    /// </summary>
    public static GraphNode Node(int id, string name, string type, string? wikiUrl = null, Continuity continuity = Continuity.Canon) =>
        new()
        {
            PageId = id,
            Name = name,
            Type = type,
            Continuity = continuity,
            Realm = Realm.Starwars,
            WikiUrl = wikiUrl,
        };

    public static async Task DisposeAsync()
    {
        if (_container is null)
            return;
        await _container.DisposeAsync();
        _container = null;
        _client = null;
    }
}
