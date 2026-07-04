namespace Arius.Api.Jobs;

/// <summary>Absolute-state progress snapshot for a job — the payload for the coalesced Progress emit and for
/// snapshot-on-attach (Plan 2). Everything is a total, so a consumer applies latest-wins with no accumulation.</summary>
public sealed record JobSnapshot
{
    public required string  JobId          { get; init; }
    public required string  Phase          { get; init; }
    public required long    TotalBytes     { get; init; }
    public required long    TotalNewBytes  { get; init; }   // TotalBytes - DedupedBytes (0 until known)
    public required long    ScannedBytes   { get; init; }
    public required long    HashedBytes    { get; init; }
    public required long    UploadedBytes  { get; init; }   // original units
    public required long    DedupedBytes   { get; init; }
    public required long    DedupedFiles   { get; init; }
    public required long?   EtaSeconds     { get; init; }   // null until TotalNewBytes known
    public required double  ThroughputBytesPerSec { get; init; }
    public required int     Pct            { get; init; }   // byte-weighted; legacy consumers read this
    public required int     WarningCount   { get; init; }
    public required IReadOnlyDictionary<string, string> Stats { get; init; }   // legacy drawer stat grid

    // ── Restore progress (zero on archive jobs) ─────────────────────────────
    public long RestoreTotalFiles          { get; init; }
    public long FilesRestored              { get; init; }
    public long RestoreTotalBytes          { get; init; }
    public long BytesRestored              { get; init; }
    public int  ChunksAvailable            { get; init; }
    public int  ChunksRehydrated           { get; init; }
    public int  ChunksNeedingRehydration   { get; init; }
    public int  ChunksPending              { get; init; }
}

/// <summary>Compact, terminal, list-friendly completion summary (persisted to the jobs `outcome` column).</summary>
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
