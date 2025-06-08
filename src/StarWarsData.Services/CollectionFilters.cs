using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;

namespace StarWarsData.Services;

public class CollectionFilters : Dictionary<string, FilterDefinition<BsonDocument>>
{
    public CollectionFilters(
        IOptions<SettingsOptions> settingsOptions,
        MongoDefinitions mongoDefinitions
    )
    {
        var regexAndFilter = Builders<BsonDocument>.Filter.And(mongoDefinitions.DataLinksContentYear, mongoDefinitions.DataLinksContentYear);

        foreach (var collection in settingsOptions.Value.TimelineCollections.Distinct())
        {
            Add(collection, regexAndFilter);
        }
    }
}