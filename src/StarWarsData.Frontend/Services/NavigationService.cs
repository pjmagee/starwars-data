namespace StarWarsData.Frontend.Services;

public class NavigationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private List<string>? _timelineCategories;

    public NavigationService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<List<string>> GetTimelineCategoriesAsync()
    {
        if (_timelineCategories == null)
        {
            var httpClient = _httpClientFactory.CreateClient("StarWarsData");
            _timelineCategories =
                await httpClient.GetFromJsonAsync<List<string>>("Timeline/available-categories")
                ?? [];
        }
        return _timelineCategories;
    }

    public void ClearCache()
    {
        _timelineCategories = null;
    }
}
