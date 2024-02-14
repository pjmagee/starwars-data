using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using StarWarsData.Models;
using StarWarsData.Models.Mongo;

namespace StarWarsData.Services.Wiki;

public class InfoboxRelationshipProcessor
{
    private readonly ILogger<InfoboxRelationshipProcessor> _logger;
    private readonly Settings _settings;
    
    private const string WikiFragment = "/wiki/";

    private readonly JsonSerializerOptions _options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };
    
    public InfoboxRelationshipProcessor(ILogger<InfoboxRelationshipProcessor> logger, Settings settings)
    {
        _logger = logger;
        _settings = settings;
    }
    
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        List<Loaded> allFiles = new DirectoryInfo(_settings.DataDirectory)		
            .EnumerateFiles("*.json", new EnumerationOptions { RecurseSubdirectories = true })
            .AsParallel()
            .WithCancellation(cancellationToken)
            .WithExecutionMode(ParallelExecutionMode.ForceParallelism)
            .Select(file =>
            {
                using var stream = file.OpenRead();
                var record = JsonSerializer.Deserialize<Record>(stream, _options)!;
                            
                string url = record.PageUrl.Split(WikiFragment).Last().Replace(" ", "_").ToLower();
                        
                return new Loaded
                {
                    PageId = int.Parse(file.Name.Split(' ')[0].Trim()),
                    File = file, 
                    Record = record,
                    Links = record.Data.SelectMany(x => x.Links).Select(x => x.Href.Split(WikiFragment).Last().ToLower()).Distinct().ToHashSet(),
                    Url = url
                };
            })
            .ToList();

        await Parallel.ForEachAsync(allFiles, new ParallelOptions { MaxDegreeOfParallelism = -1, CancellationToken = cancellationToken }, (file, token) => ProcessMentions(file, allFiles, token));
    }
    
    private async ValueTask ProcessMentions(Loaded loaded, List<Loaded> files, CancellationToken token)
    {
        loaded.Record.Relationships.Clear();
        loaded.Record.Relationships.AddRange(files.Where(other => other.Links.Contains(loaded.Url)).Select(x => new Relationship(x)));

        if (loaded.Record.Relationships.Any())
        {
            await using var stream = loaded.File.OpenWrite();
            await JsonSerializer.SerializeAsync(stream, loaded.Record, _options, token);
            await stream.FlushAsync(token);
        }
	    
        loaded.Processed = true;
	    
        _logger.LogInformation($"{files.Count(f => f.Processed)} / {files.Count}");
    }
}