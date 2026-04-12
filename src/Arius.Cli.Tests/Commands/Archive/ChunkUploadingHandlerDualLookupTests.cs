using Arius.Cli.Commands.Archive;
using Arius.Core.Features.ArchiveCommand;
using Shouldly;

namespace Arius.Cli.Tests.Commands.Archive;

/// <summary>
/// Verifies the dual-lookup behavior of <see cref="ChunkUploadingHandler"/>:
/// large files via <see cref="ProgressState.ContentHashToPath"/> and TAR bundles
/// via <see cref="ProgressState.TrackedTars"/>.
/// </summary>
public class ChunkUploadingHandlerDualLookupTests
{
    [Test]
    public async Task DualLookup_LargeFile_TakesPrecedence()
    {
        var state      = new ProgressState();
        var hashingH   = new FileHashingHandler(state);
        var hashedH    = new FileHashedHandler(state);
        var uploadingH = new ChunkUploadingHandler(state);
        var competingTar = new TrackedTar(1, state.TarTargetSize)
        {
            TarHash = "largehash",
            TotalBytes = 10_000_000,
            State = TarState.Sealing,
        };
        state.TrackedTars.TryAdd(competingTar.BundleNumber, competingTar);

        await hashingH.Handle(new FileHashingEvent("big.bin", 10_000_000), CancellationToken.None);
        await hashedH.Handle(new FileHashedEvent("big.bin", "largehash"), CancellationToken.None);

        await uploadingH.Handle(new ChunkUploadingEvent("largehash", 10_000_000), CancellationToken.None);

        state.TrackedFiles["big.bin"].State.ShouldBe(FileState.Uploading);
        state.TrackedTars[1].State.ShouldBe(TarState.Sealing);
        state.FilesUnique.ShouldBe(1L);
    }

    [Test]
    public async Task DualLookup_TarBundle_TransitionsToUploading_NoFilesUniqueIncrement()
    {
        var state      = new ProgressState();
        var startedH   = new TarBundleStartedHandler(state);
        var sealingH   = new TarBundleSealingHandler(state);
        var uploadingH = new ChunkUploadingHandler(state);

        await startedH.Handle(new TarBundleStartedEvent(), CancellationToken.None);
        await sealingH.Handle(
            new TarBundleSealingEvent(1, 100, "tarhash99", ["h1"]),
            CancellationToken.None);

        await uploadingH.Handle(new ChunkUploadingEvent("tarhash99", 100), CancellationToken.None);

        state.TrackedTars[1].State.ShouldBe(TarState.Uploading);
        state.FilesUnique.ShouldBe(0L);  // TAR path does NOT increment FilesUnique
    }
}
