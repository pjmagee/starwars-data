using MongoDB.Bson.Serialization;
using StarWarsData.Models;

namespace StarWarsData.Services;

public class RecordClassMap : BsonClassMap<Record>
{
    public RecordClassMap()
    {
        this.MapIdProperty(x => x.PageId);
        
        this.MapProperty(x => x.PageUrl);
        this.MapProperty(x => x.TemplateUrl);
        this.MapProperty(x => x.ImageUrl);
        
        this.MapProperty(x => x.Data);
        
        this.UnmapProperty(x => x.Template);
        this.UnmapProperty(x => x.PageTitle);
        
        this.MapProperty(x => x.Relationships);
    }
}