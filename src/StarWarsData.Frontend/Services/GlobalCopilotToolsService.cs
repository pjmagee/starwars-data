using Microsoft.Extensions.AI;

namespace StarWarsData.Frontend.Services;

public interface IGlobalCopilotToolFactory
{
    PageAction CreateAction();
}

public sealed class GlobalCopilotToolsService
{
    public IReadOnlyList<PageAction> Actions { get; }
    public IReadOnlyList<AIFunction> Tools { get; }

    public GlobalCopilotToolsService(IEnumerable<IGlobalCopilotToolFactory> factories)
    {
        Actions = [.. factories.Select(f => f.CreateAction())];
        Tools = [.. Actions.Select(a => a.Tool)];
    }
}

public static class GlobalCopilotToolsServiceCollectionExtensions
{
    public static IServiceCollection AddGlobalCopilotTool<TFactory>(this IServiceCollection services)
        where TFactory : class, IGlobalCopilotToolFactory => services.AddScoped<IGlobalCopilotToolFactory, TFactory>();
}
