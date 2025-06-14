using Hangfire.Dashboard;

namespace StarWarsData.ApiService;

public class AllowAllAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        // Allow all requests for development
        return true;
    }
}
