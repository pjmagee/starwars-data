using AngleSharp.Dom;

namespace StarWarsData.Services;

internal static class Extensions
{
    public static void Do(this IHtmlCollection<IElement> items, Action<IElement> action)
    {
        foreach (var item in items)
        {
            action(item);
        }
    }
}