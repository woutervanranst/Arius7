using System.Security.Cryptography;
using System.Text;

namespace Arius.Core.Models;

public readonly record struct BlobHash(string Value)
{
    public static BlobHash FromBytes(ReadOnlySpan<byte> data)
    {
        var hashBytes = SHA256.HashData(data);
        var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        return new BlobHash(hash);
    }

    public static BlobHash FromString(string value) => new(value.ToLowerInvariant());

    public override string ToString() => Value;
}

public readonly record struct PackId(string Value)
{
    public static PackId New() => new(Guid.NewGuid().ToString("N"));
    public override string ToString() => Value;
}

public readonly record struct SnapshotId(string Value)
{
    public static SnapshotId New() => new(Guid.NewGuid().ToString("N"));
    public override string ToString() => Value;
}

public readonly record struct TreeHash(string Value)
{
    public static TreeHash Empty { get; } = new("empty");
    public override string ToString() => Value;
}

public readonly record struct RepoId(string Value)
{
    public static RepoId New() => new(Guid.NewGuid().ToString("N"));
    public override string ToString() => Value;
}
