namespace Arius.Api.Shared.Jobs;

/// <summary>A job's progress, sent as the <c>Progress</c> message and returned on attach. Every field is a total,
/// so a consumer keeps the latest snapshot and never accumulates.</summary>
public sealed record JobSnapshot
{
    /// <summary>Job ID.</summary>
    public required string  JobId          { get; init; }
    /// <summary>Current phase: <c>unknown</c> before any progress is recorded, then <c>starting</c>; for an archive <c>scan</c>, <c>hash-route</c>, <c>upload</c> or <c>snapshot</c>; for a restore or download <c>classify</c>, <c>confirm-cost</c>, <c>download</c>, <c>rehydrate</c> or <c>cleanup</c>.</summary>
    public required string  Phase          { get; init; }
    /// <summary>Current job status, so clients see status changes without reloading; empty if unknown.</summary>
    public          string  Status         { get; init; } = "";
    /// <summary>Archive: total size of the scanned files, in bytes.</summary>
    public required long    TotalBytes     { get; init; }
    /// <summary>Archive: size of the new chunks queued for upload so far, in bytes.</summary>
    public required long    TotalNewBytes  { get; init; }
    /// <summary>Archive: size of the files scanned so far, in bytes.</summary>
    public required long    ScannedBytes   { get; init; }
    /// <summary>Archive: number of files scanned so far, including files that only have a pointer file and add no bytes.</summary>
    public required long    ScannedFiles   { get; init; }
    /// <summary>Archive: bytes hashed so far.</summary>
    public required long    HashedBytes    { get; init; }
    /// <summary>Archive: original (uncompressed) size of the data uploaded so far, in bytes.</summary>
    public required long    UploadedBytes  { get; init; }
    /// <summary>Archive: bytes that needed no upload because their content was already stored.</summary>
    public required long    DedupedBytes   { get; init; }
    /// <summary>Archive: number of files whose content was already stored.</summary>
    public required long    DedupedFiles   { get; init; }
    /// <summary>Estimated time remaining, in seconds, or <c>null</c> until it can be estimated.</summary>
    public required long?   EtaSeconds     { get; init; }
    /// <summary>Rate of the busiest activity, in bytes per second.</summary>
    public required double  ThroughputBytesPerSec { get; init; }
    /// <summary>Hashing rate, in bytes per second; 0 before hashing starts and after it ends.</summary>
    public          double  HashThroughputBytesPerSec   { get; init; }
    /// <summary>Transfer rate, in bytes per second: the upload rate for an archive, the download rate for a restore; 0 when idle.</summary>
    public          double  UploadThroughputBytesPerSec { get; init; }
    /// <summary>Progress from 0 to 100, weighted by bytes.</summary>
    public required int     Pct            { get; init; }
    /// <summary>Archive: true until the exact amount of new data is known, so the estimate can still change in either direction.</summary>
    public          bool    EtaIsProvisional { get; init; }
    /// <summary>Number of warnings so far.</summary>
    public required int     WarningCount   { get; init; }
    /// <summary>Summary figures for display, as label/value pairs.</summary>
    public required IReadOnlyDictionary<string, string> Stats { get; init; }

    // Restore progress; zero on archive jobs.
    /// <summary>Restore: number of files to restore.</summary>
    public long RestoreTotalFiles          { get; init; }
    /// <summary>Restore: number of files restored so far.</summary>
    public long FilesRestored              { get; init; }
    /// <summary>Restore: total size of the files to restore, in bytes.</summary>
    public long RestoreTotalBytes          { get; init; }
    /// <summary>Restore: bytes restored so far.</summary>
    public long BytesRestored              { get; init; }
    /// <summary>Restore: chunks that can be downloaded now.</summary>
    public int  ChunksAvailable            { get; init; }
    /// <summary>Restore: archive-tier chunks that have been rehydrated.</summary>
    public int  ChunksRehydrated           { get; init; }
    /// <summary>Restore: archive-tier chunks that still need a rehydration request.</summary>
    public int  ChunksNeedingRehydration   { get; init; }
    /// <summary>Restore: archive-tier chunks whose rehydration is in progress.</summary>
    public int  ChunksPending              { get; init; }
    /// <summary>Restore: number of distinct chunks the restore needs.</summary>
    public int  ChunksTotal                { get; init; }
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
