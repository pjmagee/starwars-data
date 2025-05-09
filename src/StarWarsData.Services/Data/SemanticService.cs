using System.Linq;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using Microsoft.Extensions.Logging;
using StarWarsData.Models;
using StarWarsData.Models.Queries;
using Microsoft.SemanticKernel;

namespace StarWarsData.Services.Data;

public class SemanticService
{
    private readonly Kernel _kernel;
    private readonly IMongoCollection<BsonDocument> _collection;
    private readonly ILogger<SemanticService> _logger;

    public SemanticService(Kernel kernel, Settings settings, ILogger<SemanticService> logger)
    {
        _kernel = kernel;
        _logger = logger;
        var client = new MongoClient(settings.MongoConnectionString);
        var db = client.GetDatabase(settings.MongoDbName);
        _collection = db.GetCollection<BsonDocument>("Record");
    }

    public async Task<ChartData<int>> QueryAsync(string prompt, int page = 1, int pageSize = 10)
    {
        // TODO: Implement the query logic
        throw new NotImplementedException();
    }
}
