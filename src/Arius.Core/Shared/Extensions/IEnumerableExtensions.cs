namespace Arius.Core.Shared.Extensions;

internal static class IEnumerableExtensions
{
    extension<T>(IEnumerable<T> source)
    {
        public bool Empty() => !source.Any();
    }
}