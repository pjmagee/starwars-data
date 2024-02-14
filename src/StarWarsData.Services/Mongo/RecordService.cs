using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Mongo;
using StarWarsData.Models.Queries;

namespace StarWarsData.Services.Mongo;

public class RecordService
{
    private readonly ILogger<RecordService> _logger;
    private readonly Settings _settings;
    private readonly MongoClient _mongoClient;
    private readonly IMongoDatabase _mongoDb;

    public RecordService(ILogger<RecordService> logger, Settings settings)
    {
        _logger = logger;
        _settings = settings;
        _mongoClient = new MongoClient(settings.MongoConnectionString);
        _mongoDb = _mongoClient.GetDatabase(settings.MongoDbName);
    }

    public async Task<List<string>> GetCollections(CancellationToken cancellationToken)
    {
        List<string>? results = (await (await _mongoClient
                .GetDatabase(_settings.MongoDbName)
                .ListCollectionNamesAsync(options: null, cancellationToken: cancellationToken))
            .ToListAsync(cancellationToken)).OrderBy(x => x).ToList();

        return results;
    }

    public async Task<PagedResult> GetSearchResult(string query, int page = 1, int pageSize = 50, CancellationToken token = default)
    {
        var results = new ConcurrentBag<Record>();

        var collectionNames = await (await _mongoDb.ListCollectionNamesAsync(cancellationToken: token)).ToListAsync(token);

        await Parallel.ForEachAsync(collectionNames, new ParallelOptions { CancellationToken = token }, async (name, t) =>
        {
            var collection = _mongoDb.GetCollection<Record>(name);

            var cursor = await collection.FindAsync(new FilterDefinitionBuilder<Record>().Text(query), cancellationToken: t);
            var collectionResults = await cursor.ToListAsync(t);

            foreach (var result in collectionResults)
            {
                results.Add(result);
            }
        });

        return new PagedResult
        {
            Total = results.Count,
            Size = pageSize,
            Page = page,
            Items = results.Skip((page - 1) * pageSize).Take(pageSize)
        };
    }

    public async Task<PagedResult> GetCollectionResult(string collectionName, string? searchText = null, int page = 1, int pageSize = 50, CancellationToken token = default)
    {
        return await GetPagerResultAsync(page, pageSize, _mongoDb.GetCollection<Record>(collectionName), searchText, token);
    }

    private static async Task<PagedResult> GetPagerResultAsync(int page, int pageSize, IMongoCollection<Record> collection, string? searchText, CancellationToken token = default)
    {
        var total = await collection.CountDocumentsAsync(record => true, cancellationToken: token);

        var data = await collection
            .Find(searchText is null ? FilterDefinition<Record>.Empty : new FilterDefinitionBuilder<Record>().Text(searchText))
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(token);

        return new PagedResult
        {
            Total = (int)total,
            Size = pageSize,
            Page = page,
            Items = data
        };
    }

    public async Task PopulateAsync(CancellationToken cancellationToken)
    {
        IMongoDatabase starWars = _mongoClient.GetDatabase(_settings.MongoDbName)!;

        foreach (var templateDirectoryInfo in new DirectoryInfo(_settings.DataDirectory).EnumerateDirectories())
        {
            _logger.LogInformation("Populating {Name}", templateDirectoryInfo.Name);

            await starWars.DropCollectionAsync(templateDirectoryInfo.Name, cancellationToken);
            await starWars.CreateCollectionAsync(templateDirectoryInfo.Name, cancellationToken: cancellationToken);

            var collection = starWars.GetCollection<Record>(templateDirectoryInfo.Name);
            await collection.Indexes.DropAllAsync(cancellationToken);

            await Parallel.ForEachAsync(templateDirectoryInfo.EnumerateFiles(), new ParallelOptions { CancellationToken = cancellationToken }, async (file, token) =>
            {
                await using var jsonStream = file.OpenRead();
                Record record = (await JsonSerializer.DeserializeAsync<Record>(jsonStream, cancellationToken: token))!;
                await collection.InsertOneAsync(record, new InsertOneOptions { BypassDocumentValidation = false }, token);
            });

            var indexModel = new CreateIndexModel<Record>(Builders<Record>.IndexKeys.Text("$**"));

            var index = await collection.Indexes.CreateOneAsync(indexModel, cancellationToken: cancellationToken);

            _logger.LogInformation("Index: {Index} created for {Name}", index, templateDirectoryInfo.Name);
        }

        _logger.LogInformation("Db Populated");
    }
}