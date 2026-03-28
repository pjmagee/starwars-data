using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace StarWarsData.Services;

public class MongoDefinitions
{
    public MongoDefinitions()
    {
        DataLinksHrefYear = Builders<BsonDocument>.Filter.Regex(
            "Data.Links.Href",
            new BsonRegularExpression(new Regex("_(ABY|BBY)"))
        );
        DataLinksContentYear = Builders<BsonDocument>.Filter.Regex(
            "Data.Links.Content",
            new BsonRegularExpression(new Regex(".*\\s+(ABY|BBY)"))
        );
    }

    public FilterDefinition<BsonDocument> DataLinksContentYear { get; }

    public FilterDefinition<BsonDocument> DataLinksHrefYear { get; }
}
