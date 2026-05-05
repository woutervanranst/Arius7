using Arius.Core.Shared;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Paths;
using Arius.Core.Shared.Snapshot;
using Arius.Tests.Shared.Fixtures;

namespace Arius.Core.Tests.Shared;

public class RepositoryPathsTests
{
    [Test]
    public void RepositoryDirectories_AreDerivedUnderUserProfile()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = LocalRootPath.Parse(Path.Combine(home, ".arius", "account-container"));
        var repositoryDirectoryMethod = typeof(RepositoryPaths).GetMethod(nameof(RepositoryPaths.GetRepositoryDirectory))!;
        var chunkIndexDirectoryMethod = typeof(RepositoryPaths).GetMethod(nameof(RepositoryPaths.GetChunkIndexCacheDirectory))!;
        var fileTreeDirectoryMethod = typeof(RepositoryPaths).GetMethod(nameof(RepositoryPaths.GetFileTreeCacheDirectory))!;
        var snapshotDirectoryMethod = typeof(RepositoryPaths).GetMethod(nameof(RepositoryPaths.GetSnapshotCacheDirectory))!;
        var logsDirectoryMethod = typeof(RepositoryPaths).GetMethod(nameof(RepositoryPaths.GetLogsDirectory))!;

        repositoryDirectoryMethod.ReturnType.ShouldBe(typeof(LocalRootPath));
        chunkIndexDirectoryMethod.ReturnType.ShouldBe(typeof(LocalRootPath));
        fileTreeDirectoryMethod.ReturnType.ShouldBe(typeof(LocalRootPath));
        snapshotDirectoryMethod.ReturnType.ShouldBe(typeof(LocalRootPath));
        logsDirectoryMethod.ReturnType.ShouldBe(typeof(LocalRootPath));

        ((LocalRootPath)repositoryDirectoryMethod.Invoke(null, ["account", "container"])!).ShouldBe(root);
        ((LocalRootPath)chunkIndexDirectoryMethod.Invoke(null, ["account", "container"])!).ShouldBe(LocalRootPath.Parse(Path.Combine(root.ToString(), "chunk-index")));
        ((LocalRootPath)fileTreeDirectoryMethod.Invoke(null, ["account", "container"])!).ShouldBe(LocalRootPath.Parse(Path.Combine(root.ToString(), "filetrees")));
        ((LocalRootPath)snapshotDirectoryMethod.Invoke(null, ["account", "container"])!).ShouldBe(LocalRootPath.Parse(Path.Combine(root.ToString(), "snapshots")));
        ((LocalRootPath)logsDirectoryMethod.Invoke(null, ["account", "container"])!).ShouldBe(LocalRootPath.Parse(Path.Combine(root.ToString(), "logs")));
    }

    [Test]
    public void TypedPathHelpers_DoNotExposeStringCompatibilityShortcuts()
    {
        typeof(LocalRootPath)
            .GetMethod("op_Implicit", [typeof(LocalRootPath)])
            .ShouldBeNull();

        typeof(SnapshotService)
            .GetMethod(nameof(SnapshotService.GetDiskCacheDirectory))!
            .ReturnType
            .ShouldBe(typeof(LocalRootPath));

        typeof(FileTreeStagingSession)
            .GetMethod(nameof(FileTreeStagingSession.OpenAsync), [typeof(string), typeof(CancellationToken)])
            .ShouldBeNull();

        typeof(FileTreePaths)
            .GetMethod(nameof(FileTreePaths.GetCachePath), [typeof(string), typeof(string)])
            .ShouldBeNull();

        typeof(FileTreePaths)
            .GetMethod(nameof(FileTreePaths.GetCachePath), [typeof(string), typeof(Arius.Core.Shared.Hashes.FileTreeHash)])
            .ShouldBeNull();
    }

    [Test]
    public void FileTreeService_UsesTypedFileTreeHashCachePaths()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var source = File.ReadAllText(Path.Combine(repoRoot, "src", "Arius.Core", "Shared", "FileTree", "FileTreeService.cs"));

        source.ShouldNotContain("FileTreePaths.GetCachePath(_diskCacheDir, hash.ToString())");
        source.ShouldNotContain("FileTreePaths.GetCachePath(_diskCacheDir, payload.Hash.ToString())");
        source.ShouldContain("FileTreePaths.GetCachePath(_diskCacheDir, hash)");
        source.ShouldContain("FileTreePaths.GetCachePath(_diskCacheDir, payload.Hash)");
    }

    [Test]
    public void Task3Callers_KeepTypedRepositoryRootsUntilStringBoundaries()
    {
        var repositoryFixtureConstructor = typeof(RepositoryTestFixture).GetConstructor(
            [
                typeof(Arius.Core.Shared.Storage.IBlobContainerService),
                typeof(Arius.Core.Shared.Encryption.IEncryptionService),
                typeof(Arius.Core.Shared.ChunkIndex.ChunkIndexService),
                typeof(Arius.Core.Shared.ChunkStorage.IChunkStorageService),
                typeof(Arius.Core.Shared.FileTree.FileTreeService),
                typeof(Arius.Core.Shared.Snapshot.SnapshotService),
                typeof(string),
                typeof(LocalRootPath),
                typeof(LocalRootPath),
                typeof(string),
                typeof(string),
                typeof(Action<string>)
            ]);

        repositoryFixtureConstructor.ShouldNotBeNull();
        typeof(RepositoryTestFixture)
            .GetConstructor(
                [
                    typeof(Arius.Core.Shared.Storage.IBlobContainerService),
                    typeof(Arius.Core.Shared.Encryption.IEncryptionService),
                    typeof(Arius.Core.Shared.ChunkIndex.ChunkIndexService),
                    typeof(Arius.Core.Shared.ChunkStorage.IChunkStorageService),
                    typeof(Arius.Core.Shared.FileTree.FileTreeService),
                    typeof(Arius.Core.Shared.Snapshot.SnapshotService),
                    typeof(string),
                    typeof(string),
                    typeof(string),
                    typeof(string),
                    typeof(string),
                    typeof(Action<string>)
                ])
            .ShouldBeNull();

        typeof(RepositoryTestFixture).GetProperty(nameof(RepositoryTestFixture.LocalRootPath))!.PropertyType.ShouldBe(typeof(LocalRootPath));
        typeof(RepositoryTestFixture).GetProperty(nameof(RepositoryTestFixture.RestoreRootPath))!.PropertyType.ShouldBe(typeof(LocalRootPath));

        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var archiveTestEnvironmentSource = File.ReadAllText(Path.Combine(repoRoot, "src", "Arius.Core.Tests", "Features", "ArchiveCommand", "ArchiveTestEnvironment.cs"));
        archiveTestEnvironmentSource.ShouldNotContain("private readonly string                            _rootDirectory;");
        archiveTestEnvironmentSource.ShouldNotContain("FileTreeCacheDirectory => RepositoryPaths.GetFileTreeCacheDirectory(AccountName, _containerName).ToString()");

        var pipelineFixtureSource = File.ReadAllText(Path.Combine(repoRoot, "src", "Arius.Integration.Tests", "Pipeline", "PipelineFixture.cs"));
        pipelineFixtureSource.ShouldNotContain("public string LocalRoot => _repository.LocalRoot;");
        pipelineFixtureSource.ShouldNotContain("public string RestoreRoot => _repository.RestoreRoot;");
        pipelineFixtureSource.ShouldContain("public string LocalRoot => LocalRootPath.ToString();");
        pipelineFixtureSource.ShouldContain("public string RestoreRoot => RestoreRootPath.ToString();");

        var e2eFixtureSource = File.ReadAllText(Path.Combine(repoRoot, "src", "Arius.E2E.Tests", "Fixtures", "E2EFixture.cs"));
        e2eFixtureSource.ShouldNotContain("string localRoot,");
        e2eFixtureSource.ShouldNotContain("string restoreRoot,");
        e2eFixtureSource.ShouldNotContain("repository.LocalRoot, repository.RestoreRoot");
    }
}
