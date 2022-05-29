using System.Text.Json;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using StarWarsData.Models;

namespace StarWarsData.Services;

public class RecordsService
{
    private readonly ILogger<RecordsService> _logger;
    private readonly Settings _settings;
    private readonly MongoClient _mongoClient;

    private IMongoDatabase _mongoDb;

    public RecordsService(ILogger<RecordsService> logger, Settings settings)
    {
        _logger = logger;
        _settings = settings;
        _mongoClient = new MongoClient(settings.MongoDbUri);
        _mongoDb = _mongoClient.GetDatabase(settings.MongoDbName);
    }

    public async Task<List<string>> GetCollections(CancellationToken cancellationToken)
    {
        List<string>? results = await (await _mongoClient
                .GetDatabase(_settings.MongoDbName)
                .ListCollectionNamesAsync(options: null, cancellationToken: cancellationToken))
            .ToListAsync(cancellationToken);

        return results;
    }

    public async Task<PagedResult> GetCollectionResult(string collectionName, int page = 1, int pageSize = 50, CancellationToken token = default)
    {
        return await GetPagerResultAsync(page, pageSize, _mongoDb.GetCollection<Record>(collectionName), token);
    }

    private static async Task<PagedResult> GetPagerResultAsync(int page, int pageSize, IMongoCollection<Record> collection, CancellationToken token)
    {
        var dataFacet = AggregateFacet.Create("dataFacet", PipelineDefinition<Record, Record>.Create(new[]
        {		
            PipelineStageDefinitionBuilder.Skip<Record>((page - 1) * pageSize),
            PipelineStageDefinitionBuilder.Limit<Record>(pageSize),
        }));

        var aggregation = await collection
            .Aggregate()
            .Match(Builders<Record>.Filter.Empty)
            .Facet(dataFacet)
            .ToListAsync(token);

        var data = aggregation.First().Facets.First(x => x.Name == "dataFacet").Output<Record>();

        return new PagedResult
        {
            Count = await collection.CountDocumentsAsync(record => true, cancellationToken: token),
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
            _logger.LogInformation($"Populating {templateDirectoryInfo.Name}");
            
            await starWars.DropCollectionAsync(templateDirectoryInfo.Name, cancellationToken);
            await starWars.CreateCollectionAsync(templateDirectoryInfo.Name, cancellationToken: cancellationToken);
            
            var collection = starWars.GetCollection<Record>(templateDirectoryInfo.Name);
            
            await collection.Indexes.DropAllAsync(cancellationToken);

            await Parallel.ForEachAsync(templateDirectoryInfo.EnumerateFiles(), new ParallelOptions(){  CancellationToken = cancellationToken }, async (file, token) =>
            {
                await using var jsonStream = file.OpenRead();
                
                Record record = (await JsonSerializer.DeserializeAsync<Record>(jsonStream, cancellationToken: token))!;
                var filter = Builders<Record>.Filter.Eq(f => f.PageId, record.PageId);
                var options = new FindOneAndReplaceOptions<Record>() { IsUpsert = true };
                        
                await collection.FindOneAndReplaceAsync(filter, record, options, token);
            });
            
            var indexModel = new CreateIndexModel<Record>(Builders<Record>.IndexKeys
                .Text("Data.Values")
                .Text("Data.Links.Content")
                .Text("Data.Label"));
                
            await collection.Indexes.CreateOneAsync(indexModel, cancellationToken: cancellationToken);
            
            _logger.LogInformation($"Indexed {templateDirectoryInfo.Name}");
        }
        
        _logger.LogInformation($"Db Populated");
    }
}