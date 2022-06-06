using MongoDB.Bson.Serialization;
using StarWarsData.Models;

namespace StarWarsData.Services;

public class RecordClassMap : BsonClassMap<Record>
{
    public RecordClassMap()
    {
        MapIdProperty(x => x.PageId);
        
        MapProperty(x => x.PageUrl);
        MapProperty(x => x.TemplateUrl);
        MapProperty(x => x.ImageUrl);
        
        MapProperty(x => x.Data);
        
        UnmapProperty(x => x.Template);
        UnmapProperty(x => x.PageTitle);
        
        MapProperty(x => x.Relationships);
    }
}