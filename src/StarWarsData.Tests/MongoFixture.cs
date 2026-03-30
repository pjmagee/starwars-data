using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Services;
using Testcontainers.MongoDb;

namespace StarWarsData.Tests;

/// <summary>
/// Shared MongoDB container fixture. One container is started for the entire
/// "Mongo" xUnit collection, seeded once with the Skywalker test dataset.
/// </summary>
public sealed class MongoFixture : IAsyncLifetime
{
    private MongoDbContainer _container = null!;

    public RelationshipGraphService Service { get; private set; } = null!;

    private const string DbName = "test-pages";

    public async Task InitializeAsync()
    {
        _container = new MongoDbBuilder("mongo:8").Build();

        await _container.StartAsync();

        var client = new MongoClient(_container.GetConnectionString());
        var coll = client
            .GetDatabase(DbName)
            .GetCollection<StarWarsData.Models.Entities.Page>(Collections.Pages);
        await coll.InsertManyAsync(RelationshipGraphServiceTests.BuildDataset());

        var settings = Options.Create(new SettingsOptions { DatabaseName = DbName });
        Service = new RelationshipGraphService(
            NullLogger<RelationshipGraphService>.Instance,
            settings,
            client
        );
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition("Mongo")]
public class MongoCollection : ICollectionFixture<MongoFixture> { }
