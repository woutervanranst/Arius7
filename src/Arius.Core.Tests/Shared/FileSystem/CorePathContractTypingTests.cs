using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.ChunkHydrationStatusQuery;
using Arius.Core.Features.ListQuery;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;

namespace Arius.Core.Tests.Shared.FileSystem;

public class CorePathContractTypingTests
{
    [Test]
    public void DomainContracts_UseRelativePathForRepositoryPaths()
    {
        typeof(RestoreOptions).GetProperty(nameof(RestoreOptions.TargetPath))!.PropertyType
            .ShouldBe(typeof(RelativePath?));

        typeof(ListQueryOptions).GetProperty(nameof(ListQueryOptions.Prefix))!.PropertyType
            .ShouldBe(typeof(RelativePath?));

        typeof(ArchiveCommandOptions).GetProperty(nameof(ArchiveCommandOptions.CreateHashProgress))!.PropertyType
            .ShouldBe(typeof(Func<RelativePath, long, IProgress<long>>));

        typeof(RepositoryEntry).GetProperty(nameof(RepositoryEntry.RelativePath))!.PropertyType
            .ShouldBe(typeof(RelativePath));

        typeof(RepositoryFileEntry).GetProperty(nameof(RepositoryFileEntry.RelativePath))!.PropertyType
            .ShouldBe(typeof(RelativePath));

        typeof(RepositoryDirectoryEntry).GetProperty(nameof(RepositoryDirectoryEntry.RelativePath))!.PropertyType
            .ShouldBe(typeof(RelativePath));

        typeof(ChunkHydrationStatusResult).GetProperty(nameof(ChunkHydrationStatusResult.RelativePath))!.PropertyType
            .ShouldBe(typeof(RelativePath));

        typeof(FileScannedEvent).GetProperty(nameof(FileScannedEvent.RelativePath))!.PropertyType
            .ShouldBe(typeof(RelativePath));

        typeof(FileHashingEvent).GetProperty(nameof(FileHashingEvent.RelativePath))!.PropertyType
            .ShouldBe(typeof(RelativePath));

        typeof(FileHashedEvent).GetProperty(nameof(FileHashedEvent.RelativePath))!.PropertyType
            .ShouldBe(typeof(RelativePath));

        typeof(FileRestoredEvent).GetProperty(nameof(FileRestoredEvent.RelativePath))!.PropertyType
            .ShouldBe(typeof(RelativePath));

        typeof(FileSkippedEvent).GetProperty(nameof(FileSkippedEvent.RelativePath))!.PropertyType
            .ShouldBe(typeof(RelativePath));

        typeof(FileDispositionEvent).GetProperty(nameof(FileDispositionEvent.RelativePath))!.PropertyType
            .ShouldBe(typeof(RelativePath));

        typeof(LocalFileState).GetProperty(nameof(LocalFileState.Name))!.PropertyType
            .ShouldBe(typeof(PathSegment));
    }

    [Test]
    public void SharedServices_DoNotKeepRedundantRootedStringCacheFields()
    {
        typeof(FileTreeService).GetField("_diskCacheDir", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .ShouldBeNull();
        typeof(FileTreeService).GetField("_snapshotsDir", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .ShouldBeNull();
        typeof(FileTreeService).GetField("_chunkIndexL2Dir", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .ShouldBeNull();
        typeof(SnapshotService).GetField("_diskCacheDir", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .ShouldBeNull();
        typeof(ChunkIndexService).GetField("_l2Dir", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .ShouldBeNull();
    }

    [Test]
    public void SnapshotApis_KeepBlobNamesTypedInsideCore()
    {
        typeof(BlobAlreadyExistsException).GetProperty(nameof(BlobAlreadyExistsException.BlobName))!
            .PropertyType
            .ShouldBe(typeof(RelativePath));

        typeof(SnapshotService).GetMethod(nameof(SnapshotService.BlobName))!
            .ReturnType
            .ShouldBe(typeof(RelativePath));

        typeof(SnapshotService).GetMethod(nameof(SnapshotService.ListBlobNamesAsync))!
            .ReturnType
            .ShouldBe(typeof(Task<IReadOnlyList<RelativePath>>));

        typeof(Shard).GetMethod(nameof(Shard.PrefixOf))!
            .ReturnType
            .ShouldBe(typeof(PathSegment));

        typeof(BlobPaths).GetMethod(nameof(BlobPaths.ChunkIndexShardPath), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .GetParameters()[0]
            .ParameterType
            .ShouldBe(typeof(PathSegment));

        typeof(FileTreePaths)
            .GetMethod(nameof(FileTreePaths.GetCachePath), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public, [typeof(string)])
            .ShouldBeNull();
    }

    [Test]
    public async Task SharedServices_RouteCacheRootCreationThroughRelativeFileSystem()
    {
        var coreRoot = GetCoreRoot();

        var chunkIndexSource = await File.ReadAllTextAsync(Path.Combine(coreRoot, "Shared", "ChunkIndex", "ChunkIndexService.cs"));
        var snapshotSource = await File.ReadAllTextAsync(Path.Combine(coreRoot, "Shared", "Snapshot", "SnapshotService.cs"));
        var fileTreeSource = await File.ReadAllTextAsync(Path.Combine(coreRoot, "Shared", "FileTree", "FileTreeService.cs"));

        chunkIndexSource.ShouldNotContain("Directory.CreateDirectory(");
        snapshotSource.ShouldNotContain("Directory.CreateDirectory(");
        fileTreeSource.ShouldNotContain("Directory.CreateDirectory(");
    }

    [Test]
    public async Task SharedServices_DoNotDowngradeTypedBlobPathsBackToStringsInternally()
    {
        var coreRoot = GetCoreRoot();

        var snapshotSource = await File.ReadAllTextAsync(Path.Combine(coreRoot, "Shared", "Snapshot", "SnapshotService.cs"));
        var fileTreeSource = await File.ReadAllTextAsync(Path.Combine(coreRoot, "Shared", "FileTree", "FileTreeService.cs"));
        var chunkIndexSource = await File.ReadAllTextAsync(Path.Combine(coreRoot, "Shared", "ChunkIndex", "ChunkIndexService.cs"));
        var listQuerySource = await File.ReadAllTextAsync(Path.Combine(coreRoot, "Features", "ListQuery", "ListQueryHandler.cs"));
        var archiveSource = await File.ReadAllTextAsync(Path.Combine(coreRoot, "Features", "ArchiveCommand", "ArchiveCommandHandler.cs"));
        var archiveModelsSource = await File.ReadAllTextAsync(Path.Combine(coreRoot, "Features", "ArchiveCommand", "Models.cs"));
        var localFileSource = await File.ReadAllTextAsync(Path.Combine(coreRoot, "Shared", "LocalFile", "LocalFileEnumerator.cs"));

        snapshotSource.ShouldNotContain("candidate.Name.ToString()");
        snapshotSource.ShouldNotContain("RelativePath.Parse(blobName.Name.ToString())");
        snapshotSource.ShouldNotContain("RelativePath.Parse(fileName)");
        fileTreeSource.ShouldNotContain("Select(name => name.ToString())");
        fileTreeSource.ShouldNotContain("RelativePath.Parse(blobName.Name.ToString())");
        fileTreeSource.ShouldNotContain("RelativePath.Parse(hashText)");
        chunkIndexSource.ShouldNotContain("RelativePath.Parse(prefix)");
        listQuerySource.ShouldNotContain("opts.Prefix?.ToString()");
        listQuerySource.ShouldNotContain("currentRelativeDirectory.ToString()");
        listQuerySource.ShouldNotContain("relativePath.ToString()");
        listQuerySource.ShouldNotContain("candidate.Entry.Name.ToString()");
        listQuerySource.ShouldNotContain("localFile.Name.ToString()");
        listQuerySource.ShouldNotContain("e.Name.ToString(), segment.ToString()");
        archiveSource.ShouldNotContain("pair.Path.ToString()");
        archiveSource.ShouldNotContain("hashed.FilePair.Path.ToString()");
        archiveSource.ShouldNotContain("upload.HashedPair.FilePair.Path.ToString()");
        archiveSource.ShouldNotContain("Path.GetTempFileName()");
        archiveSource.ShouldNotContain("File.OpenRead(currentTarPath");
        archiveSource.ShouldNotContain("File.OpenRead(sealed_.TarFilePath)");
        archiveSource.ShouldNotContain("File.Delete(sealed_.TarFilePath)");
        archiveModelsSource.ShouldNotContain("string          TarFilePath");
        localFileSource.ShouldNotContain("path.ToString()");
    }

    [Test]
    public void FileTreeStagingApis_UseTypedLocalRootsAndRelativePaths()
    {
        typeof(IFileTreeStagingSession).GetProperty(nameof(IFileTreeStagingSession.StagingRoot))!.PropertyType
            .ShouldBe(typeof(LocalDirectory));

        typeof(FileTreeStagingSession)
            .GetMethod(nameof(FileTreeStagingSession.OpenAsync), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, [typeof(LocalDirectory), typeof(CancellationToken)])!
            .ShouldNotBeNull();

        typeof(FileTreeBuilder)
            .GetMethod(nameof(FileTreeBuilder.SynchronizeAsync), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, [typeof(LocalDirectory), typeof(CancellationToken)])!
            .ShouldNotBeNull();

        typeof(FileTreeStagingWriter)
            .GetMethod(nameof(FileTreeStagingWriter.AppendFileEntryAsync), [typeof(RelativePath), typeof(Arius.Core.Shared.Hashes.ContentHash), typeof(DateTimeOffset), typeof(DateTimeOffset), typeof(CancellationToken)])
            .ShouldNotBeNull();

        typeof(FileTreeStagingWriter)
            .GetMethod(nameof(FileTreeStagingWriter.AppendFileEntryAsync), [typeof(string), typeof(Arius.Core.Shared.Hashes.ContentHash), typeof(DateTimeOffset), typeof(DateTimeOffset), typeof(CancellationToken)])
            .ShouldBeNull();

        typeof(Arius.Core.Shared.LocalFile.LocalFileEnumerator)
            .GetMethod(nameof(Arius.Core.Shared.LocalFile.LocalFileEnumerator.Enumerate), [typeof(LocalDirectory)])
            .ShouldNotBeNull();

        typeof(Arius.Core.Shared.LocalFile.LocalFileEnumerator)
            .GetMethod(nameof(Arius.Core.Shared.LocalFile.LocalFileEnumerator.Enumerate), [typeof(string)])
            .ShouldBeNull();
    }

    private static string GetCoreRoot()
    {
        var assemblyDirectory = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!;
        return Path.GetFullPath(Path.Combine(assemblyDirectory, "..", "..", "..", "..", "Arius.Core"));
    }

}
