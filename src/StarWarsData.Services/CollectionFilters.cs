using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;

namespace StarWarsData.Services;

public class CollectionFilters : Dictionary<string, FilterDefinition<BsonDocument>>
{
    private readonly FilterDefinition<BsonDocument> _containsYearValueFilter = Builders<BsonDocument>.Filter.Regex("Data.Links.Content", new BsonRegularExpression(new Regex(".*\\s+(ABY|BBY)")));
    private readonly FilterDefinition<BsonDocument> _containsYearLinkFilter = Builders<BsonDocument>.Filter.Regex("Data.Links.Href", new BsonRegularExpression(new Regex("_(ABY|BBY)")));
    
    private FilterDefinition<BsonDocument> GenericDateRegexFilter => Builders<BsonDocument>.Filter.And(_containsYearValueFilter, _containsYearLinkFilter);

    public CollectionFilters(Settings settings)
    {
        foreach (var collection in settings.TimelineCollections.Distinct())
        {
            Add(collection, GenericDateRegexFilter);
        }
    }
}