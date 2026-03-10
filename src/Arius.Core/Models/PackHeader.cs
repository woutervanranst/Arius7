namespace Arius.Core.Models;

public sealed record PackHeaderEntry(
    BlobHash BlobHash,
    BlobType BlobType,
    long Offset,
    long Length);

public sealed record PackHeader(IReadOnlyList<PackHeaderEntry> Entries);
