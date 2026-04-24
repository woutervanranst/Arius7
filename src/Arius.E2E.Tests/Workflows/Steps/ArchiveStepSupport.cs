using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Arius.E2E.Tests.Fixtures;

namespace Arius.E2E.Tests.Workflows.Steps;

internal static class ArchiveStepSupport
{
    public static Task<ArchiveResult> ArchiveAsync(
        E2EFixture fixture,
        bool useNoPointers = false,
        bool useRemoveLocal = false,
        BlobTier uploadTier = BlobTier.Cool,
        CancellationToken cancellationToken = default)
    {
        var options = new ArchiveCommandOptions
        {
            RootDirectory = fixture.LocalRoot,
            UploadTier = uploadTier,
            NoPointers = useNoPointers,
            RemoveLocal = useRemoveLocal,
        };

        return fixture.CreateArchiveHandler().Handle(new ArchiveCommand(options), cancellationToken).AsTask();
    }

    public static string FormatSnapshotVersion(DateTimeOffset snapshotTime) =>
        snapshotTime.UtcDateTime.ToString(SnapshotService.TimestampFormat);
}
