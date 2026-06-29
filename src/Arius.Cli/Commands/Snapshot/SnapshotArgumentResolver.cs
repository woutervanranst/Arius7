using Arius.Core.Features.SnapshotsListQuery;

namespace Arius.Cli.Commands.Snapshot;

/// <summary>
/// Resolves a CLI snapshot argument to a Core version string (a <c>StartsWith</c> prefix matched by
/// <c>ISnapshotService.ResolveAsync</c>). A pure integer is a 1-based index into <paramref name="snapshots"/>
/// (oldest = 1). Anything else is a version prefix with ':' stripped, so a typed timestamp like
/// "2024-04-02T13:09:54" becomes the stored-format prefix "2024-04-02T130954".
/// </summary>
internal static class SnapshotArgumentResolver
{
    public static string Resolve(string argument, IReadOnlyList<SnapshotInfo> snapshots)
    {
        if (int.TryParse(argument, out var index))
        {
            if (index < 1 || index > snapshots.Count)
                throw new ArgumentException($"Snapshot index {index} is out of range (1..{snapshots.Count}).", nameof(argument));
            return snapshots[index - 1].Version;
        }

        return argument.Replace(":", string.Empty);
    }
}
