namespace Arius.Core.Shared.Hashes;

public readonly record struct ChunkHash
{
    private readonly string _value;

    private ChunkHash(string value) => _value = value;

    public string Prefix4 => Value[..4];

    public string Short8 => Value[..8];

    private string Value => _value ?? string.Empty;

    public static ChunkHash Parse(string value) => new(HashCodec.NormalizeHex(value));

    public static bool TryParse(string? value, out ChunkHash hash)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                hash = default;
                return false;
            }

            hash = Parse(value);
            return true;
        }
        catch (FormatException)
        {
            hash = default;
            return false;
        }
    }

    public static ChunkHash FromDigest(ReadOnlySpan<byte> digest) => new(HashCodec.ToLowerHex(digest));

    public override string ToString() => Value;
}
