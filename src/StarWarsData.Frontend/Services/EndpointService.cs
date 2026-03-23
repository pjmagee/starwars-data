namespace StarWarsData.Frontend.Services;

public class EndpointService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<EndpointService> _logger;

    public EndpointService(IConfiguration configuration, ILogger<EndpointService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public string? GetKeycloakAccountUrl()
    {
        try
        {
            var httpsEndpoint = _configuration["services:keycloak:https:0"];
            if (!string.IsNullOrEmpty(httpsEndpoint))
            {
                return $"{httpsEndpoint.TrimEnd('/')}/realms/starwars-data/account";
            }

            var httpEndpoint = _configuration["services:keycloak:http:0"];
            if (!string.IsNullOrEmpty(httpEndpoint))
            {
                return $"{httpEndpoint.TrimEnd('/')}/realms/starwars-data/account";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve Keycloak account URL");
        }

        return null;
    }

    public string? GetHangfireDashboardUrl()
    {
        try
        {
            // Try to get the external HTTPS URL for the API service
            var httpsEndpoint = _configuration["services:apiservice:https:0"];
            if (!string.IsNullOrEmpty(httpsEndpoint))
            {
                return $"{httpsEndpoint.TrimEnd('/')}/hangfire";
            }

            // Fallback to HTTP if HTTPS is not available
            var httpEndpoint = _configuration["services:apiservice:http:0"];
            if (!string.IsNullOrEmpty(httpEndpoint))
            {
                return $"{httpEndpoint.TrimEnd('/')}/hangfire";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve apiservice external URL");
        }

        return null;
    }
}
