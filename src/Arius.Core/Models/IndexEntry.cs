namespace Arius.Core.Models;

public enum BlobType
{
    Data,
    Tree
}

public sealed record IndexEntry(
    BlobHash BlobHash,
    PackId PackId,
    long Offset,
    long Length,
    BlobType BlobType);
