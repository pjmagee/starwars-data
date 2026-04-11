using MongoDB.Driver;
using Testcontainers.MongoDb;

namespace StarWarsData.Tests.Infrastructure;

/// <summary>
/// Lazy-initialized MongoDB Testcontainer used by <see cref="StarWarsData.Services.MongoCheckpointStore"/> tests.
/// Independent of <see cref="ApiFixture"/> so checkpoint tests don't carry the seed data overhead.
/// </summary>
public static class CheckpointStoreFixture
{
    public const string DbName = "test-checkpoints";

    private static readonly SemaphoreSlim Lock = new(1, 1);
    private static MongoDbContainer? _container;
    private static IMongoClient? _client;

    public static IMongoClient Client => _client ?? throw new InvalidOperationException("CheckpointStoreFixture not initialized — call EnsureInitializedAsync from [ClassInitialize]");

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

    public static async Task DisposeAsync()
    {
        if (_container is null)
            return;
        await _container.DisposeAsync();
        _container = null;
        _client = null;
    }
}
