using Arius.Cli.Commands.Archive;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared.Hashes;
using Arius.Tests.Shared.Hashes;

namespace Arius.Cli.Tests.Commands.Archive;

/// <summary>
/// Verifies the full <see cref="TrackedTar"/> lifecycle:
/// Accumulating → Sealing → Uploading → removed.
/// </summary>
public class TrackedTarLifecycleTests
{
    [Test]
    public async Task TrackedTar_FullLifecycle_StateTransitions()
    {
        var state     = new ProgressState();
        var startedH  = new TarBundleStartedHandler(state);
        var entryH    = new TarEntryAddedHandler(state);
        var sealingH  = new TarBundleSealingHandler(state);
        var uploadingH = new ChunkUploadingHandler(state);
        var uploadedH = new TarBundleUploadedHandler(state);

        // Start tar
        await startedH.Handle(new TarBundleStartedEvent(), CancellationToken.None);
        state.TrackedTars[1].State.ShouldBe(TarState.Accumulating);

        // Add files (need them in ContentHashToPath for entry handler)
        var hashingH = new FileHashingHandler(state);
        var hashedH  = new FileHashedHandler(state);
        await hashingH.Handle(new FileHashingEvent("f1.txt", 100), CancellationToken.None);
        await hashedH.Handle(new FileHashedEvent("f1.txt", HashTestData.Content('a')), CancellationToken.None);
        await hashingH.Handle(new FileHashingEvent("f2.txt", 200), CancellationToken.None);
        await hashedH.Handle(new FileHashedEvent("f2.txt", HashTestData.Content('b')), CancellationToken.None);

        await entryH.Handle(new TarEntryAddedEvent(HashTestData.Content('a'), 1, 100), CancellationToken.None);
        await entryH.Handle(new TarEntryAddedEvent(HashTestData.Content('b'), 2, 300), CancellationToken.None);

        state.TrackedTars[1].FileCount.ShouldBe(2);
        state.TrackedTars[1].AccumulatedBytes.ShouldBeGreaterThan(0);

        // Seal
        await sealingH.Handle(
            new TarBundleSealingEvent(2, 300, HashTestData.Chunk('c'), [HashTestData.Content('a'), HashTestData.Content('b')]),
            CancellationToken.None);
        state.TrackedTars[1].State.ShouldBe(TarState.Sealing);
        state.TrackedTars[1].TarHash.ShouldBe(HashTestData.Chunk('c'));
        state.TrackedTars[1].TotalBytes.ShouldBe(300L);

        // Upload starts
        await uploadingH.Handle(new ChunkUploadingEvent(HashTestData.Chunk('c'), 300), CancellationToken.None);
        state.TrackedTars[1].State.ShouldBe(TarState.Uploading);

        // Upload complete → removed
        await uploadedH.Handle(new TarBundleUploadedEvent(HashTestData.Chunk('c'), 200, 2), CancellationToken.None);
        state.TrackedTars.ContainsKey(1).ShouldBeFalse();
        state.TarsUploaded.ShouldBe(1L);
    }
}
