namespace Arius.Api.Contracts;

public sealed record SnapshotDto(string Version, DateTimeOffset Timestamp, long FileCount);
