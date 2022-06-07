using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;

namespace StarWarsData.Services.Mongo;

public class CollectionFilters : Dictionary<string, FilterDefinition<BsonDocument>>
{
    public CollectionFilters(Settings settings, MongoDefinitions mongoDefinitions)
    {
        var regexAndFilter = Builders<BsonDocument>.Filter.And(mongoDefinitions.DataLinksContentYear, mongoDefinitions.DataLinksContentYear);
        
        foreach (var collection in settings.TimelineCollections.Distinct())
        {
            Add(collection, regexAndFilter);
        }
    }
}