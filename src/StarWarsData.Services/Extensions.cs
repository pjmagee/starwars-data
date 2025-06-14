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

    public static void Do<T>(this IEnumerable<T> items, Action<T> action)
    {
        foreach (var item in items)
        {
            action(item);
        }
    }
}
