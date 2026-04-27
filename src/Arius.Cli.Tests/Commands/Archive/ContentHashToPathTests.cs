using Arius.Core.Shared.Hashes;

namespace Arius.Cli.Tests.Commands.Archive;

/// <summary>
/// Verifies the <see cref="ProgressState.ContentHashToPath"/> reverse map is populated
/// on hash and used for downstream events keyed by content hash.
/// </summary>
public class ContentHashToPathTests
{
    private static ContentHash Content(char c) => ContentHash.Parse(new string(c, 64));

    [Test]
    public void ReverseMap_PopulatedOnHash_UsedForDownstreamEvents()
    {
        var state = new ProgressState();

        state.AddFile("dir/file.bin", 500);
        state.SetFileHashed("dir/file.bin", Content('a'));

        // Reverse map populated
        state.ContentHashToPath.ContainsKey(Content('a')).ShouldBeTrue();
        state.ContentHashToPath[Content('a')].ShouldContain("dir/file.bin");

        // Downstream event via reverse map: SetFileUploading transitions Hashed → Uploading
        state.SetFileUploading(Content('a'));
        state.TrackedFiles["dir/file.bin"].State.ShouldBe(FileState.Uploading);
    }

    [Test]
    public void ReverseMap_PopulatedBeforeDownstreamEvents()
    {
        // FileHashedEvent sets both ContentHash and reverse map atomically
        var state = new ProgressState();
        state.AddFile("config.yml", 200);
        state.SetFileHashed("config.yml", Content('b'));

        state.ContentHashToPath.ContainsKey(Content('b')).ShouldBeTrue();
    }
}
