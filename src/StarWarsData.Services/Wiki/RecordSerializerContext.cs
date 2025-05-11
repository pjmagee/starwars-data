using System.Text.Json.Serialization;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services.Wiki;

[JsonSerializable(typeof(Record), GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSourceGenerationOptions(AllowTrailingCommas = false, WriteIndented = true)]
public partial class RecordSerializerContext : JsonSerializerContext
{
    
}
