namespace Arius.Core.Shared.Hashes;

/// <summary>
/// Helpers for the various Hash* types (structs cannot inherit)
/// </summary>
[SharedWithinAssembly]
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
        var alreadyCanonical = true;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            switch (c)
            {
                case >= '0' and <= '9':
                case >= 'a' and <= 'f':
                    chars[i] = c;
                    break;
                case >= 'A' and <= 'F':
                    chars[i]         = char.ToLowerInvariant(c);
                    alreadyCanonical = false;
                    break;
                default:
                    throw new FormatException($"Invalid hex character '{c}'.");
            }
        }

        // The dominant case is re-parsing a value Arius itself wrote (SQLite, snapshot JSON, blob names,
        // pointer files), which is already canonical lowercase. Returning the input then avoids allocating
        // a second identical string. Validation above has still run over every character.
        return alreadyCanonical ? value : new string(chars);
    }

    public static string ToLowerHex(ReadOnlySpan<byte> digest)
    {
        if (digest.Length != Sha256ByteLength)
            throw new ArgumentException($"Expected {Sha256ByteLength}-byte SHA-256 digest.", nameof(digest));

        // Not Convert.ToHexString(...).ToLowerInvariant(): that allocates the uppercase string and then a
        // second lowercased copy. This is the funnel every ContentHash/ChunkHash/FileTreeHash construction
        // passes through.
        return Convert.ToHexStringLower(digest);
    }
}
