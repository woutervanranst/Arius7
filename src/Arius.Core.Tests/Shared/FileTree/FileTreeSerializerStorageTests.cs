using Arius.Core.Shared.FileTree;

namespace Arius.Core.Tests.Shared.FileTree;

public class FileTreeSerializerStorageTests
{
    [Test]
    public void Serialize_ProducesPlaintextUtf8Bytes()
    {
        IReadOnlyList<FileTreeEntry> entries =
        [
            new FileEntry
            {
                Name = "photo.jpg",
                ContentHash = Arius.Core.Shared.Hashes.ContentHash.Parse(new string('a', 64)),
                Created = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero),
                Modified = new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero)
            },
            new DirectoryEntry
            {
                Name = "subdir/",
                FileTreeHash = Arius.Core.Shared.Hashes.FileTreeHash.Parse(new string('e', 64))
            }
        ];

        var bytes = FileTreeSerializer.Serialize(entries);
        var text = System.Text.Encoding.UTF8.GetString(bytes);

        text.ShouldContain("photo.jpg");
        text.ShouldContain("subdir/");
        text.ShouldContain(" F ");
        text.ShouldContain(" D ");
    }
}
