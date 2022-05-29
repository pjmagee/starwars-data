using System.Text.Json;
using StarWarsData.Models;
using Statiq.App;
using Statiq.Common;
using Statiq.Core;
using Statiq.Razor;

await Bootstrapper
    .Factory
    .CreateDefault(args)
    .AddMappedInputPath(NormalizedPath.Up.Combine(NormalizedPath.Up).Combine(new NormalizedPath("data")), "input/data")
    .SetOutputPath(NormalizedPath.Up.Combine(NormalizedPath.Up).Combine(new NormalizedPath("output")))
    .BuildPipeline("StarWars", builder =>
    {
        builder
            .WithInputModules(
                new ReadFiles("input/data/Era/*.json"),
                new TakeDocuments(100)
            );
        
        builder
            .WithProcessModules(
                new ParseJson(),
                new SetMetadata("Record", Config.FromDocument(c =>
                {
                    using var reader = c.GetContentTextReader();
                    return JsonDocument.Parse(reader.ReadToEnd()).Deserialize<Record>();
                })),
                new SetContent(string.Empty),
                new SetDestination(Config.FromDocument(c => new NormalizedPath($"{c.Get<Record>("Record").PageId}.html"))),
                new SetMediaType(MediaTypes.Razor)
            );

        builder.WithPostProcessModules(
            new RenderRazor()
                .WithLayout("_Layout")
                .WithModel(Config.FromDocument(d => d.Get<Record>("Record"))));

        builder.WithOutputModules(
            new WriteFiles()
        );
    })
    .RunAsync();