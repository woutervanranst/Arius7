using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Tests.Shared.FileTree;

public class FileTreeSerializerStorageTests
{
    private static readonly DateTimeOffset s_created  = new(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_modified = new(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<FileTreeEntry> MakeEntries() =>
    [
        new FileEntry { Name = "photo.jpg", ContentHash = ContentHash.Parse(NormalizeHash("a1b2c3d4")), Created = s_created, Modified = s_modified },
        new DirectoryEntry { Name = "subdir/", FileTreeHash = FileTreeHash.Parse(NormalizeHash("e5f6a7b8")) }
    ];

    private static string NormalizeHash(string hash)
        => hash.Length == 64 ? hash : hash[0].ToString().PadRight(64, char.ToLowerInvariant(hash[0]));

    [Test]
    public async Task SerializeForStorage_WithPassphrase_ThenDeserialize_RoundTrips()
    {
        var enc = new PassphraseEncryptionService("test-passphrase");
        var entries = MakeEntries();

        var bytes = await FileTreeSerializer.SerializeForStorageAsync(entries, enc);
        var back = await FileTreeSerializer.DeserializeFromStorageAsync(new MemoryStream(bytes), enc);

        back.Count.ShouldBe(2);
        var fileEntry = back.Single(e => e.Name == "photo.jpg").ShouldBeOfType<FileEntry>();
        var dirEntry = back.Single(e => e.Name == "subdir/").ShouldBeOfType<DirectoryEntry>();

        fileEntry.ContentHash.ShouldBe(ContentHash.Parse(NormalizeHash("a1b2c3d4")));
        fileEntry.Created.ShouldBe(s_created);
        fileEntry.Modified.ShouldBe(s_modified);

        dirEntry.FileTreeHash.ShouldBe(FileTreeHash.Parse(NormalizeHash("e5f6a7b8")));
    }

    [Test]
    public async Task SerializeForStorage_WithPlaintext_ThenDeserialize_RoundTrips()
    {
        var enc = new PlaintextPassthroughService();
        var entries = MakeEntries();

        var bytes = await FileTreeSerializer.SerializeForStorageAsync(entries, enc);
        var back = await FileTreeSerializer.DeserializeFromStorageAsync(new MemoryStream(bytes), enc);

        back.Count.ShouldBe(2);
        var fileEntry = back.Single(e => e.Name == "photo.jpg").ShouldBeOfType<FileEntry>();
        var dirEntry = back.Single(e => e.Name == "subdir/").ShouldBeOfType<DirectoryEntry>();

        fileEntry.ContentHash.ShouldBe(ContentHash.Parse(NormalizeHash("a1b2c3d4")));
        fileEntry.Created.ShouldBe(s_created);
        fileEntry.Modified.ShouldBe(s_modified);

        dirEntry.FileTreeHash.ShouldBe(FileTreeHash.Parse(NormalizeHash("e5f6a7b8")));
    }

    [Test]
    public async Task SerializeForStorage_WithPassphrase_StartsWithArGcm1Magic()
    {
        var enc = new PassphraseEncryptionService("test-passphrase");
        var entries = MakeEntries();

        var bytes = await FileTreeSerializer.SerializeForStorageAsync(entries, enc);

        var prefix = System.Text.Encoding.ASCII.GetString(bytes[..6]);
        prefix.ShouldBe("ArGCM1");
    }

    [Test]
    public async Task SerializeForStorage_WithPassphrase_OutputIsNotPlaintext()
    {
        var enc = new PassphraseEncryptionService("test-passphrase");
        var entries = MakeEntries();

        var bytes = await FileTreeSerializer.SerializeForStorageAsync(entries, enc);
        var text = System.Text.Encoding.UTF8.GetString(bytes);

        text.ShouldNotContain("photo.jpg");
        text.ShouldNotContain("subdir/");
    }
}
