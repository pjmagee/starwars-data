using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace StarWarsData.Frontend.Components.Shared;

public class LoggingErrorBoundary : ErrorBoundary
{
    [Inject]
    private ILogger<LoggingErrorBoundary> Logger { get; set; } = default!;

    [Inject]
    private NavigationManager Nav { get; set; } = default!;

    protected override Task OnErrorAsync(Exception ex)
    {
        Logger.LogError(ex, "Unhandled component exception on {Url}", Nav.Uri);
        return Task.CompletedTask;
    }
}
