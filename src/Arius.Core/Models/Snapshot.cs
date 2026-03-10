namespace Arius.Core.Models;

public sealed record Snapshot(
    SnapshotId Id,
    DateTimeOffset Time,
    TreeHash Tree,
    IReadOnlyList<string> Paths,
    string Hostname,
    string Username,
    IReadOnlyList<string> Tags,
    SnapshotId? Parent);
