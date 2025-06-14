using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services;

public class InfoboxRelationshipProcessor
{
    readonly ILogger<InfoboxRelationshipProcessor> _logger;
    readonly IMongoDatabase _rawDb;

    const string WikiFragment = "/wiki/";

    public InfoboxRelationshipProcessor(
        ILogger<InfoboxRelationshipProcessor> logger,
        IOptions<SettingsOptions> settingsOptions,
        IMongoClient mongoClient
    )
    {
        _logger = logger;
        _rawDb = mongoClient.GetDatabase(settingsOptions.Value.InfoboxDb);
    }

    public async Task CreateRelationshipsAsync(CancellationToken cancellationToken)
    {
        // Gather all records across template-based collections in raw DB
        var collectionNames = await (
            await _rawDb.ListCollectionNamesAsync(cancellationToken: cancellationToken)
        ).ToListAsync(cancellationToken);
        var allFiles = new List<Loaded>();
        foreach (var name in collectionNames)
        {
            var collection = _rawDb.GetCollection<Infobox>(name);
            var records = await collection
                .Find(FilterDefinition<Infobox>.Empty)
                .ToListAsync(cancellationToken);
            allFiles.AddRange(
                records.Select(record =>
                {
                    string url = record
                        .PageUrl.Split(WikiFragment)
                        .Last()
                        .Replace(" ", "_")
                        .ToLower();
                    return new Loaded
                    {
                        PageId = record.PageId,
                        Record = record,
                        Links = record
                            .Data.SelectMany(x => x.Links)
                            .Select(x => x.Href.Split(WikiFragment).Last().ToLower())
                            .Distinct()
                            .ToHashSet(),
                        Url = url,
                    };
                })
            );
        }
        // Process relationships in parallel
        await Parallel.ForEachAsync(
            allFiles,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = -1,
                CancellationToken = cancellationToken,
            },
            (file, token) => ProcessMentions(file, allFiles, token)
        );
    }

    async ValueTask ProcessMentions(Loaded loaded, List<Loaded> files, CancellationToken token)
    {
        loaded.Record.Relationships.Clear();
        loaded.Record.Relationships.AddRange(
            files.Where(other => other.Links.Contains(loaded.Url)).Select(x => new Relationship(x))
        );

        if (loaded.Record.Relationships.Any())
        {
            var collection = _rawDb.GetCollection<Infobox>(loaded.Record.Template);
            var filter = Builders<Infobox>.Filter.Eq(x => x.PageId, loaded.Record.PageId);
            await collection.ReplaceOneAsync(filter, loaded.Record, cancellationToken: token);
        }

        loaded.Processed = true;

        _logger.LogInformation(
            "{Count} / {FilesCount}",
            files.Count(f => f.Processed),
            files.Count
        );
    }
}
