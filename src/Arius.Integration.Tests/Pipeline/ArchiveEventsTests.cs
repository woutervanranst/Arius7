using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared.Storage;
using Arius.Tests.Shared.Fixtures;
using NSubstitute;

namespace Arius.Integration.Tests.Pipeline;

/// <summary>
/// Tests that the archive pipeline publishes byte-accurate progress events: the uploaded chunk's
/// original (uncompressed) size on <see cref="ChunkUploadedEvent"/>, and a <see cref="FileDedupedEvent"/>
/// for content that is deduplicated rather than re-uploaded.
/// </summary>
[ClassDataSource<AzuriteFixture>(Shared = SharedType.PerTestSession)]
public class ArchiveEventsTests(AzuriteFixture azurite)
{
    [Test]
    public async Task ChunkUploaded_carries_original_size_and_dedup_fires_for_duplicate_content()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        // Two files, identical content of a known size → one large-chunk upload, one dedup.
        // SmallFileThreshold is lowered to the content size so both files route through the
        // large-file pipeline (the one that publishes ChunkUploadedEvent) instead of tar-bundling.
        const long size = 4096;
        var content = new byte[size];
        Random.Shared.NextBytes(content);
        await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("a.bin"), content, CancellationToken.None);
        await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("b.bin"), content, CancellationToken.None);

        var archiveResult = await fix.ArchiveAsync(new ArchiveCommandOptions
        {
            RootDirectory      = fix.LocalDirectory.ToString(),
            UploadTier         = BlobTier.Hot,
            SmallFileThreshold = size,
        });
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);
        archiveResult.FilesUploaded.ShouldBe(1);
        archiveResult.FilesDeduped.ShouldBe(1);

        // Exactly one chunk is uploaded (the duplicate is deduplicated), carrying the original
        // (uncompressed) size of the source file.
        await fix.Mediator.Received(1).Publish(
            Arg.Is<ChunkUploadedEvent>(e => e.OriginalSize == size),
            Arg.Any<CancellationToken>());

        // The duplicate file fires FileDedupedEvent with the same original size.
        await fix.Mediator.Received(1).Publish(
            Arg.Is<FileDedupedEvent>(e => e.OriginalSize == size),
            Arg.Any<CancellationToken>());
    }
}
