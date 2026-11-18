namespace Arius.Api.Features.Snapshots;

public sealed record SnapshotDto(string Version, DateTimeOffset Timestamp, long FileCount);
