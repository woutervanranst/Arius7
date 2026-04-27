using Arius.Core.Shared.Hashes;

namespace Arius.Cli.Tests.Commands.Archive;

/// <summary>
/// Verifies <see cref="TrackedFile"/> state machine for the large-file/direct-upload path:
/// Hashing → Hashed → Uploading → Done (removed).
/// </summary>
public class TrackedFileLargeFilePathTests
{
    private static ContentHash Content(char c) => ContentHash.Parse(new string(c, 64));

    [Test]
    public void LargeFilePath_StateTransitions_Correct()
    {
        var state = new ProgressState();

        // FileHashingEvent
        state.AddFile("video.mp4", 5_000_000_000L);
        state.TrackedFiles["video.mp4"].State.ShouldBe(FileState.Hashing);

        // FileHashedEvent → State=Hashed (invisible)
        state.SetFileHashed("video.mp4", Content('a'));
        state.TrackedFiles["video.mp4"].ContentHash.ShouldBe(Content('a').ToString());
        state.TrackedFiles["video.mp4"].State.ShouldBe(FileState.Hashed);

        // ChunkUploadingEvent → SetFileUploading (only Hashed files promoted to Uploading)
        state.SetFileUploading(Content('a'));
        state.TrackedFiles["video.mp4"].State.ShouldBe(FileState.Uploading);

        // ChunkUploadedEvent → RemoveFile
        state.RemoveFile("video.mp4");
        state.TrackedFiles.ContainsKey("video.mp4").ShouldBeFalse();
    }

    [Test]
    public void SetFileUploading_OnlyPromotesHashedFiles()
    {
        // A file in Hashing state (not yet Hashed) should not transition to Uploading.
        var state = new ProgressState();
        state.AddFile("pending.txt", 100);
        // Don't call SetFileHashed — still in Hashing state
        state.TrackedFiles["pending.txt"].State.ShouldBe(FileState.Hashing);

        // No ContentHashToPath entry yet, so SetFileUploading won't find it
        state.SetFileUploading(Content('b'));
        state.TrackedFiles["pending.txt"].State.ShouldBe(FileState.Hashing);
    }
}
