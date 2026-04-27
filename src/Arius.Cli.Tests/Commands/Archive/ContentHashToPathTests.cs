using Arius.Core.Shared.Hashes;
using Arius.Tests.Shared.Hashes;

namespace Arius.Cli.Tests.Commands.Archive;

/// <summary>
/// Verifies the <see cref="ProgressState.ContentHashToPath"/> reverse map is populated
/// on hash and used for downstream events keyed by content hash.
/// </summary>
public class ContentHashToPathTests
{
    [Test]
    public void ReverseMap_PopulatedOnHash_UsedForDownstreamEvents()
    {
        var state = new ProgressState();

        state.AddFile("dir/file.bin", 500);
        state.SetFileHashed("dir/file.bin", HashTestData.Content('a'));

        // Reverse map populated
        state.ContentHashToPath.ContainsKey(HashTestData.Content('a')).ShouldBeTrue();
        state.ContentHashToPath[HashTestData.Content('a')].ShouldContain("dir/file.bin");

        // Downstream event via reverse map: SetFileUploading transitions Hashed → Uploading
        state.SetFileUploading(HashTestData.Content('a'));
        state.TrackedFiles["dir/file.bin"].State.ShouldBe(FileState.Uploading);
    }

    [Test]
    public void ReverseMap_PopulatedBeforeDownstreamEvents()
    {
        // FileHashedEvent sets both ContentHash and reverse map atomically
        var state = new ProgressState();
        state.AddFile("config.yml", 200);
        state.SetFileHashed("config.yml", HashTestData.Content('b'));

        state.ContentHashToPath.ContainsKey(HashTestData.Content('b')).ShouldBeTrue();
    }
}
