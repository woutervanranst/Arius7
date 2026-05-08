using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.ChunkHydrationStatusQuery;
using Arius.Core.Features.ListQuery;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;

namespace Arius.Core.Tests.Shared.FileSystem;

public class CorePathContractTypingTests
{
    [Test]
    public void DomainContracts_UseRelativePathForRepositoryPaths()
    {
        typeof(ListQueryOptions).GetProperty(nameof(ListQueryOptions.Prefix))!.PropertyType
            .ShouldBe(typeof(RelativePath?));

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

}
