using Arius.Core.Shared.Hashes;

namespace Arius.Cli.Tests.Commands.Archive;

/// <summary>
/// Verifies <see cref="TrackedFile"/> state machine for the small-file/tar path:
/// Hashing → Hashed → removed from TrackedFiles when added to TAR.
/// </summary>
public class TrackedFileSmallFilePathTests
{
    [Test]
    public void SmallFilePath_StateTransitions_Correct()
    {
        var state = new ProgressState();

        // FileHashingEvent → AddFile → State=Hashing
        state.AddFile("notes.txt", 1024);
        state.TrackedFiles.ContainsKey("notes.txt").ShouldBeTrue();
        state.TrackedFiles["notes.txt"].State.ShouldBe(FileState.Hashing);

        // FileHashedEvent → SetFileHashed → State=Hashed, reverse map populated
        state.SetFileHashed("notes.txt", FakeContentHash('d'));
        state.TrackedFiles["notes.txt"].ContentHash.ShouldBe(FakeContentHash('d').ToString());
        state.TrackedFiles["notes.txt"].State.ShouldBe(FileState.Hashed);
        state.ContentHashToPath[FakeContentHash('d')].ShouldContain("notes.txt");
        state.FilesHashed.ShouldBe(1L);

        // TarEntryAddedEvent → RemoveFile (small file moves into TAR)
        state.RemoveFile("notes.txt");
        state.TrackedFiles.ContainsKey("notes.txt").ShouldBeFalse();
    }
}
