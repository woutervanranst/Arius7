using System.Collections.Immutable;

namespace Arius.Core.Shared;

public static class IEnumerableExtensions
{
    public static bool Empty<T>(this IEnumerable<T> str) => !str.Any();
}

public static class StreamExtensions
{
    /// <summary>
    /// Materializes the current <see cref="MemoryStream"/> contents as an <see cref="ImmutableArray{T}"/>.
    /// It reuses the underlying buffer when the stream exposes one; otherwise it allocates a new array copy.
    /// The returned array contains exactly the bytes in the stream payload up to <see cref="MemoryStream.Length"/>.
    /// </summary>
    public static ImmutableArray<byte> ToImmutableArray(this MemoryStream stream)
    {
        if (stream.TryGetBuffer(out ArraySegment<byte> segment))
            return ImmutableArray.Create(segment.Array!, segment.Offset, segment.Count);

        // Fallback if the MemoryStream doesn't expose its buffer (e.g. if it was created with a non-zero offset or is not expandable)
        return [..stream.ToArray()];

        // NOTE: Unsafe alternative: var sealedBytes = Unsafe.As<MemoryStream, ImmutableArray<byte>>(ref sealedStream);
    }

    /// <summary>
    /// Returns the current <see cref="MemoryStream"/> contents as an <see cref="ArraySegment{T}"/> of bytes.
    /// It reuses the underlying buffer when the stream exposes one; otherwise it allocates a new array copy
    /// and returns a segment over that copy. The returned segment covers exactly the bytes in the stream
    /// payload up to <see cref="MemoryStream.Length"/>.
    /// </summary>
    public static ArraySegment<byte> ToArraySegment(this MemoryStream stream)
    {
        if (stream.TryGetBuffer(out var sealedBuffer))
            return new ArraySegment<byte>(sealedBuffer.Array!, sealedBuffer.Offset, checked((int)stream.Length));

        return new ArraySegment<byte>(stream.ToArray());
    }
}