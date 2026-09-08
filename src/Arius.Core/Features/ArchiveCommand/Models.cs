namespace Arius.Core.Features.ArchiveCommand;

// -- Models

/// <summary>
/// Represents Arius's local archive-time view of one repository path.
/// It exists to unify binary-only, pointer-only, and binary-plus-pointer cases behind one domain model,
/// with responsibility for carrying the validated relative path and its optional binary and pointer components.
/// </summary>
internal sealed record FilePair
{
    /// <summary>
    /// The relative path to the BinaryFile
    /// </summary>
    public required RelativePath RelativePath { get; init; }

    public BinaryFile? Binary { get; init; }

    public PointerFile? Pointer { get; init; }
}

/// <summary>
/// Represents the binary side of an archive-time file pair.
/// It exists so archive code can work with validated relative paths and captured file metadata instead of full host paths,
/// with responsibility for describing the binary file that may need hashing, upload, or restoration.
/// </summary>
internal sealed record BinaryFile
{
    public required RelativePath Path { get; init; }
}

/// <summary>
/// Represents a pointer file discovered beside or instead of a binary file.
/// It exists so Arius can model thin-archive state explicitly, with responsibility for carrying the pointer path,
/// the binary path it refers to, and the parsed content hash when the pointer content is valid.
/// </summary>
internal sealed record PointerFile
{
    public required RelativePath Path { get; init; }

    public ContentHash? Hash { get; init; }

    /// <summary>
    /// True when the on-disk pointer was written in the legacy (v5) JSON format. The archive command rewrites
    /// such pointers to the current format in place (preserving the hash and timestamps).
    /// </summary>
    public bool IsLegacyFormat { get; init; }
}




// ── Pipeline intermediate models ──────────────────────────────────────────────

/// <summary>
/// Represents a <see cref="FilePair"/> after Arius has computed its content hash.
/// It exists to give the archive pipeline a stable handoff between hashing and deduplication,
/// with responsibility for pairing the local file model with the resolved content identity.
///
/// The source file's timestamps are captured here, at hashing time — the moment we decide to
/// archive the file and have it open — so later stages never re-read the filesystem for metadata.
/// This keeps the snapshot consistent even if the source file is moved or deleted mid-run.
/// </summary>
internal sealed record HashedFilePair(
    FilePair       FilePair,
    ContentHash    ContentHash,
    DateTimeOffset Created,
    DateTimeOffset Modified
);

/// <summary>
/// Represents a pending archive-time pointer-file write for an archived binary file.
/// </summary>
internal sealed record PendingPointerWrite(
    RelativePath   BinaryPath,
    ContentHash    Hash,
    DateTimeOffset Created,
    DateTimeOffset Modified
);

/// <summary>
/// Represents a hashed file pair that still needs chunk upload work.
/// It exists to separate deduplicated-away files from upload candidates,
/// with responsibility for carrying the hashed local file data and the byte count needed by upload logic.
/// </summary>
internal sealed record FileToUpload(
    HashedFilePair HashedPair,
    long           FileSize     // bytes (0 for pointer-only)
);

/// <summary>
/// Represents a small file staged into the current tar chunk accumulator.
/// It exists so tar-chunk assembly can retain per-file manifest context while estimating compressed sizes,
/// with responsibility for carrying the hashed file identity plus the original size used for accounting.
/// </summary>
internal sealed record TarEntry(
    ContentHash    ContentHash,
    long           OriginalSize,
    HashedFilePair HashedPair
);

/// <summary>
/// Represents a completed tar chunk that is ready to upload.
/// It exists to hand off the in-memory tar payload together with the per-entry metadata needed for thin chunks,
/// with responsibility for describing the sealed tar content, its chunk hash, its size, and its member entries.
/// </summary>
internal sealed record SealedTar(
    ArraySegment<byte>      Content,
    ChunkHash               TarHash,            // hash of the tar body (before gzip+encrypt)
    long                    UncompressedSize,   // sum of file sizes
    IReadOnlyList<TarEntry> Entries             // per-file info for thin chunk
);
