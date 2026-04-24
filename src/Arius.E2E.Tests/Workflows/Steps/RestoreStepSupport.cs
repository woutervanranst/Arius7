using Arius.Core.Features.RestoreCommand;
using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Fixtures;

namespace Arius.E2E.Tests.Workflows.Steps;

internal static class RestoreStepSupport
{
    public static Task<RestoreResult> RestoreAsync(E2EFixture fixture, bool overwrite, string? version, CancellationToken cancellationToken)
    {
        var options = new RestoreOptions
        {
            RootDirectory = fixture.RestoreRoot,
            Overwrite = overwrite,
            Version = version,
        };

        return fixture.CreateRestoreHandler().Handle(new RestoreCommand(options), cancellationToken).AsTask();
    }

    public static async Task AssertRestoreOutcomeAsync(
        E2EFixture fixture,
        SyntheticRepositoryDefinition definition,
        SyntheticRepositoryVersion expectedVersion,
        int seed,
        bool useNoPointers,
        RestoreResult restoreResult,
        bool preserveConflictBytes)
    {
        if (preserveConflictBytes)
        {
            var conflictPath = GetConflictPath(definition, expectedVersion);
            var restoredPath = Path.Combine(fixture.RestoreRoot, conflictPath.Replace('/', Path.DirectorySeparatorChar));
            var expectedConflictBytes = CreateConflictBytes(seed, conflictPath);

            restoreResult.FilesSkipped.ShouldBeGreaterThan(0);
            (await File.ReadAllBytesAsync(restoredPath)).ShouldBe(expectedConflictBytes);
            return;
        }

        var expectedRoot = Path.Combine(Path.GetTempPath(), $"arius-expected-{Guid.NewGuid():N}");
        try
        {
            var expected = await SyntheticRepositoryMaterializer.MaterializeAsync(
                definition,
                expectedVersion,
                seed,
                expectedRoot);

            await SyntheticRepositoryStateAssertions.AssertMatchesDiskTreeAsync(expected, fixture.RestoreRoot, includePointerFiles: false);

            if (!useNoPointers)
            {
                foreach (var relativePath in expected.Files.Keys)
                {
                    var pointerPath = Path.Combine(
                        fixture.RestoreRoot,
                        (relativePath + ".pointer.arius").Replace('/', Path.DirectorySeparatorChar));

                    File.Exists(pointerPath).ShouldBeTrue($"Expected pointer file for {relativePath}");
                }
            }
        }
        finally
        {
            if (Directory.Exists(expectedRoot))
                Directory.Delete(expectedRoot, recursive: true);
        }
    }

    public static async Task WriteRestoreConflictAsync(
        E2EFixture fixture,
        SyntheticRepositoryDefinition definition,
        SyntheticRepositoryVersion expectedVersion,
        int seed)
    {
        var conflictPath = GetConflictPath(definition, expectedVersion);
        var fullPath = Path.Combine(fixture.RestoreRoot, conflictPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        var conflictBytes = CreateConflictBytes(seed, conflictPath);
        await File.WriteAllBytesAsync(fullPath, conflictBytes);
    }

    public static string? ResolveVersion(RepresentativeWorkflowState state, WorkflowRestoreTarget target) =>
        target switch
        {
            WorkflowRestoreTarget.Previous => state.PreviousSnapshotVersion ?? throw new InvalidOperationException("Previous snapshot version is not available."),
            _ => null,
        };

    static string GetConflictPath(SyntheticRepositoryDefinition definition, SyntheticRepositoryVersion expectedVersion)
    {
        const string v1ChangedPath = "src/module-00/group-00/file-0000.bin";

        if (definition.Files.Any(file => file.Path == v1ChangedPath) && expectedVersion == SyntheticRepositoryVersion.V1)
            return v1ChangedPath;

        return definition.Files[0].Path;
    }

    static byte[] CreateConflictBytes(int seed, string path)
    {
        var bytes = new byte[1024];
        new Random(HashCode.Combine(seed, path, "restore-conflict")).NextBytes(bytes);
        return bytes;
    }
}
