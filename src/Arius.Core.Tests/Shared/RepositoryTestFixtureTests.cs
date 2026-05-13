using Arius.Core.Shared.Encryption;
using Arius.Tests.Shared.Fixtures;
using Arius.Tests.Shared.Storage;
using Shouldly;

namespace Arius.Core.Tests.Shared;

public class RepositoryTestFixtureTests
{
    [Test]
    public async Task CreateWithEncryptionAsync_DoesNotDeleteCallerSuppliedTempRoot()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "arius", $"caller-temp-root-{Guid.NewGuid():N}");
        var keepFile = Path.Combine(tempRoot, "keep.txt");

        Directory.CreateDirectory(tempRoot);
        await File.WriteAllTextAsync(keepFile, "keep");

        try
        {
            await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(
                new FakeInMemoryBlobContainerService(),
                $"acct-caller-temp-{Guid.NewGuid():N}",
                $"ctr-caller-temp-{Guid.NewGuid():N}",
                new PlaintextPassthroughService(),
                tempRoot: tempRoot);

            Directory.Exists(tempRoot).ShouldBeTrue();
            File.Exists(keepFile).ShouldBeTrue();
            Directory.Exists(fixture.LocalRoot).ShouldBeTrue();
            Directory.Exists(fixture.RestoreRoot).ShouldBeTrue();

            await fixture.DisposeAsync();

            Directory.Exists(tempRoot).ShouldBeTrue();
            File.Exists(keepFile).ShouldBeTrue();
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Test]
    public async Task CreateWithEncryptionAsync_ClearsFixtureOwnedSubdirectoriesWithinCallerSuppliedTempRoot()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "arius", $"caller-temp-root-{Guid.NewGuid():N}");
        var keepFile = Path.Combine(tempRoot, "keep.txt");
        var sourceFile = Path.Combine(tempRoot, "source", "stale.bin");
        var restoreFile = Path.Combine(tempRoot, "restore", "stale.pointer.arius");

        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(restoreFile)!);
        await File.WriteAllTextAsync(keepFile, "keep");
        await File.WriteAllTextAsync(sourceFile, "source-stale");
        await File.WriteAllTextAsync(restoreFile, "restore-stale");

        try
        {
            await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(
                new FakeInMemoryBlobContainerService(),
                $"acct-caller-temp-clean-{Guid.NewGuid():N}",
                $"ctr-caller-temp-clean-{Guid.NewGuid():N}",
                new PlaintextPassthroughService(),
                tempRoot: tempRoot);

            Directory.Exists(tempRoot).ShouldBeTrue();
            File.Exists(keepFile).ShouldBeTrue();
            Directory.Exists(fixture.LocalRoot).ShouldBeTrue();
            Directory.Exists(fixture.RestoreRoot).ShouldBeTrue();
            Directory.EnumerateFileSystemEntries(fixture.LocalRoot).ShouldBeEmpty();
            Directory.EnumerateFileSystemEntries(fixture.RestoreRoot).ShouldBeEmpty();
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }
}
