namespace Arius.Core.Shared.Hashes;

public readonly record struct ContentHash
{
    private readonly string _value;

    private ContentHash(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        _value = value;
    }

    public string Prefix4 => Value[..4];

    public string Short8 => Value[..8];

    private string Value => _value ?? throw new InvalidOperationException("ContentHash is uninitialized.");

    public static ContentHash Parse(string value) => new(HashCodec.NormalizeHex(value));

    public static ContentHash Parse(ChunkHash value) => new(value.ToString());

    public static bool TryParse(string? value, out ContentHash hash)
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

    public static ContentHash FromDigest(ReadOnlySpan<byte> digest) => new(HashCodec.ToLowerHex(digest));

    public override string ToString() => Value;
}
