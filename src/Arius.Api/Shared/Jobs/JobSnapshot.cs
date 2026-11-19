namespace Arius.Api.Shared.Jobs;

/// <summary>A job's progress, sent as the <c>Progress</c> message and returned on attach. Every field is a total,
/// so a consumer keeps the latest snapshot and never accumulates.</summary>
public sealed record JobSnapshot
{
    public required string  JobId          { get; init; }
    public required string  Phase          { get; init; }
    public          string  Status         { get; init; } = "";   // live job status, so clients see transitions without a reload; empty if unknown
    public required long    TotalBytes     { get; init; }
    public required long    TotalNewBytes  { get; init; }   // bytes of the new chunks queued for upload so far
    public required long    ScannedBytes   { get; init; }
    public required long    ScannedFiles   { get; init; }   // includes pointer-only files, which add no ScannedBytes
    public required long    HashedBytes    { get; init; }
    public required long    UploadedBytes  { get; init; }   // original (uncompressed) size
    public required long    DedupedBytes   { get; init; }
    public required long    DedupedFiles   { get; init; }
    public required long?   EtaSeconds     { get; init; }   // null until TotalNewBytes is known
    public required double  ThroughputBytesPerSec { get; init; }
    public          double  HashThroughputBytesPerSec   { get; init; }   // 0 before hashing starts and after it ends
    public          double  UploadThroughputBytesPerSec { get; init; }   // upload rate for archive, download rate for restore; 0 when idle
    public required int     Pct            { get; init; }   // byte-weighted
    public          bool    EtaIsProvisional { get; init; }   // archive only: true until routing fixes the exact new-byte total, so the ETA can still move either way
    public required int     WarningCount   { get; init; }
    public required IReadOnlyDictionary<string, string> Stats { get; init; }   // job drawer stat grid

    // Restore progress; zero on archive jobs.
    public long RestoreTotalFiles          { get; init; }
    public long FilesRestored              { get; init; }
    public long RestoreTotalBytes          { get; init; }
    public long BytesRestored              { get; init; }
    public int  ChunksAvailable            { get; init; }
    public int  ChunksRehydrated           { get; init; }
    public int  ChunksNeedingRehydration   { get; init; }
    public int  ChunksPending              { get; init; }
    public int  ChunksTotal                { get; init; }   // distinct chunks, from ChunkResolutionCompleteEvent
}

/// <summary>A finished job's summary for the jobs list, persisted in the <c>jobs.outcome</c> column.</summary>
public sealed record JobOutcome
{
    public long?   FileCount         { get; init; }
    public long?   UploadedBytes     { get; init; }
    public long?   DedupedBytes      { get; init; }
    public long?   FilesRestored     { get; init; }
    public long?   DownloadedBytes   { get; init; }
    public string? SnapshotTimestamp { get; init; }
    public long?   DurationSeconds   { get; init; }
}
