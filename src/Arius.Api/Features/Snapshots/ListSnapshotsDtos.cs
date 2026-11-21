namespace Arius.Api.Features.Snapshots;

/// <summary>A snapshot: the state of the repository at the end of one archive.</summary>
/// <param name="Version">Snapshot version, derived from its creation time. Pass it, or a prefix of it, as <c>version</c> to select this snapshot.</param>
/// <param name="Timestamp">Creation time (UTC).</param>
/// <param name="FileCount">Number of files in the snapshot.</param>
public sealed record SnapshotDto(string Version, DateTimeOffset Timestamp, long FileCount);
