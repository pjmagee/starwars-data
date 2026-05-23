using StarWarsData.Models.Entities;

namespace StarWarsData.Models.Wookieepedia;

public sealed record WookieepediaArticleOpenResult(bool IsSuccess, string? Title, Continuity Continuity);
