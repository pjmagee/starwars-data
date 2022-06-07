using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace StarWarsData.Services.Mongo;

public class MongoDefinitions
{
    public FilterDefinition<BsonDocument> DataLinksContentYear { get; } = Builders<BsonDocument>.Filter.Regex("Data.Links.Content", new BsonRegularExpression(new Regex(".*\\s+(ABY|BBY)")));

    public FilterDefinition<BsonDocument> DataLinksHrefYear { get; } = Builders<BsonDocument>.Filter.Regex("Data.Links.Href", new BsonRegularExpression(new Regex("_(ABY|BBY)")));

    public ProjectionDefinition<BsonDocument> ExcludeRelationships { get; } = Builders<BsonDocument>.Projection.Exclude(doc => doc["Relationships"]);
}