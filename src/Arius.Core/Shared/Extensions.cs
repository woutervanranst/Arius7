using System.Collections.Immutable;

namespace Arius.Core.Shared;

public static class IEnumerableExtensions
{
    public static bool Empty<T>(this IEnumerable<T> str) => !str.Any();
}

public static class StreamExtensions
{
    public static ImmutableArray<byte> ToImmutableArray(this MemoryStream stream)
    {
        if (stream.TryGetBuffer(out ArraySegment<byte> segment))
            return ImmutableArray.Create(segment.Array!, segment.Offset, segment.Count);

        // Fallback if the MemoryStream doesn't expose its buffer (e.g. if it was created with a non-zero offset or is not expandable)
        return [..stream.ToArray()];

        // NOTE: Unsafe alternative: var sealedBytes = Unsafe.As<MemoryStream, ImmutableArray<byte>>(ref sealedStream);
    }
}