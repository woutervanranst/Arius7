namespace Arius.Core.Shared.Hashes;

/// <summary>
/// Helpers for the various Hash* types (structs cannot inherit)
/// </summary>
internal static class HashCodec
{
    public const int Sha256ByteLength = 32;
    public const int Sha256HexLength = Sha256ByteLength * 2;

    public static string NormalizeHex(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);

        if (value.Length != Sha256HexLength)
            throw new FormatException($"Expected {Sha256HexLength} hex characters but got {value.Length}.");

        Span<char> chars = stackalloc char[Sha256HexLength];
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            chars[i] = c switch
            {
                >= '0' and <= '9' => c,
                >= 'a' and <= 'f' => c,
                >= 'A' and <= 'F' => char.ToLowerInvariant(c),
                _ => throw new FormatException($"Invalid hex character '{c}'.")
            };
        }

        return new string(chars);
    }

    public static string ToLowerHex(ReadOnlySpan<byte> digest)
    {
        if (digest.Length != Sha256ByteLength)
            throw new ArgumentException($"Expected {Sha256ByteLength}-byte SHA-256 digest.", nameof(digest));

        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}
