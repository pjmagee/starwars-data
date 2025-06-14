using System.Text.Json.Serialization;
using StarWarsData.Models.Entities;

namespace StarWarsData.Models;

[JsonSerializable(typeof(Infobox), GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(Page), GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSourceGenerationOptions(AllowTrailingCommas = false, WriteIndented = true)]
public partial class EntitySerializerContext : JsonSerializerContext { }
