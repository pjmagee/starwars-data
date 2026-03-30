using Hangfire.Dashboard;

namespace StarWarsData.Admin;

public class AllowAllAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        // Admin app is internal-only, network-isolated — no auth needed
        return true;
    }
}
