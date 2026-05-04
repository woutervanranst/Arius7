namespace Arius.Core.Shared;

public static class IEnumerableExtensions
{
    public static bool Empty<T>(this IEnumerable<T> str) => !str.Any();
}