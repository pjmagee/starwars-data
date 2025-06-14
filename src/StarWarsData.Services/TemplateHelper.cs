namespace StarWarsData.Services;

public class TemplateHelper
{
    public string GetTemplateFromUri(string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
            return string.Empty;
        var lastColon = template.LastIndexOf(':');
        var name = lastColon >= 0 ? template[(lastColon + 1)..] : template;
        return name.Replace("_infobox", string.Empty).Replace("_", " ").Trim();
    }
}
