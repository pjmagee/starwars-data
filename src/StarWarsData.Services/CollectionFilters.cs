using MongoDB.Bson;
using MongoDB.Driver;

namespace StarWarsData.Services;

public class CollectionFilters : Dictionary<string, FilterDefinition<BsonDocument>>
{
    private readonly FilterDefinition<BsonDocument> _dateCreated = Builders<BsonDocument>.Filter.AnyEq("Data.Label", "Date created");
    private readonly FilterDefinition<BsonDocument> _date = Builders<BsonDocument>.Filter.AnyEq("Data.Label", "Date");
    
    private readonly FilterDefinition<BsonDocument> _constructedOrDestroyed = Builders<BsonDocument>.Filter.Or(
        Builders<BsonDocument>.Filter.AnyEq("Data.Label", "Constructed"), 
        Builders<BsonDocument>.Filter.AnyEq("Data.Label", "Destroyed"));
    
    private readonly FilterDefinition<BsonDocument> _discoveredOrDestroyed = Builders<BsonDocument>.Filter.Or(
        Builders<BsonDocument>.Filter.AnyEq("Data.Label", "Date discovered"), 
        Builders<BsonDocument>.Filter.AnyEq("Data.Label", "Date destroyed"));

    /// <summary>
    /// Maybe this can come from appsettings.config and loaded at app startup
    /// But really, this shouldnt be needed if we do Transformations into an Events collection...
    /// </summary>
    public CollectionFilters()
    {
        Add("Event", _date);
        Add("Election", _date);
        Add("Law", _date);
        Add("Battle", _date);
        Add("Duel", _date);
        Add("Mission", _date);
        Add("Droid", _dateCreated);
        Add("Artifact", _dateCreated);
        Add("War", Builders<BsonDocument>.Filter.AnyEq("Data.Label", "Beginning"));
        Add("Location", _constructedOrDestroyed);
        Add("City", _constructedOrDestroyed);
        Add("Campaign", Builders<BsonDocument>.Filter.AnyEq("Data.Label", "Begin"));
        Add("Character", Builders<BsonDocument>.Filter.AnyEq("Data.Label", "Born"));
        Add("Treaty", Builders<BsonDocument>.Filter.AnyEq("Data.Label", "Date established"));
        Add("Lightsaber", Builders<BsonDocument>.Filter.AnyEq("Data.Label", "Date discovered"));
        Add("Organization", Builders<BsonDocument>.Filter.Or(Builders<BsonDocument>.Filter.AnyEq("Data.Label", "Date founded"), Builders<BsonDocument>.Filter.AnyEq("Data.Label", "Formed from")));
        Add("Government", Builders<BsonDocument>.Filter.Or(Builders<BsonDocument>.Filter.AnyEq("Data.Label", "Date established"), Builders<BsonDocument>.Filter.AnyEq("Data.Label", "Formed from")));
        Add("Fleet", Builders<BsonDocument>.Filter.AnyEq("Data.Label", "Founding"));
        Add("Disease",  Builders<BsonDocument>.Filter.AnyEq("Data.Label", "Date engineered"));
        Add("Holocron_infobox", Builders<BsonDocument>.Filter.AnyEq("Data.Label", "Date discovered"));
        Add("Weapon", _discoveredOrDestroyed);
        Add("Device", _dateCreated);
        Add("Religion ", Builders<BsonDocument>.Filter.AnyEq("Data.Label", "Date founded"));
        Add("Trade_route ", Builders<BsonDocument>.Filter.AnyEq("Data.Label", "Date founded"));
    }
}