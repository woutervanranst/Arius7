using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Shouldly;

namespace Arius.Core.Tests.Shared.FileTree;

public class FileTreeBlobSerializerStorageTests
{
    private static readonly DateTimeOffset s_created  = new(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_modified = new(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private static FileTreeBlob MakeBlob() => new()
    {
        Entries =
        [
            new FileTreeEntry { Name = "photo.jpg", Type = FileTreeEntryType.File, Hash = "a1b2c3d4", Created = s_created, Modified = s_modified },
            new FileTreeEntry { Name = "subdir/",   Type = FileTreeEntryType.Dir,  Hash = "e5f6a7b8" }
        ]
    };

    [Test]
    public async Task SerializeForStorage_WithPassphrase_ThenDeserialize_RoundTrips()
    {
        var enc  = new PassphraseEncryptionService("test-passphrase");
        var blob = MakeBlob();

        var bytes  = await FileTreeBlobSerializer.SerializeForStorageAsync(blob, enc);
        var back   = await FileTreeBlobSerializer.DeserializeFromStorageAsync(new MemoryStream(bytes), enc);

        back.Entries.Count.ShouldBe(2);
        var fileEntry = back.Entries.Single(e => e.Name == "photo.jpg");
        var dirEntry = back.Entries.Single(e => e.Name == "subdir/");

        fileEntry.Hash.ShouldBe("a1b2c3d4");
        fileEntry.Type.ShouldBe(FileTreeEntryType.File);
        fileEntry.Created.ShouldBe(s_created);
        fileEntry.Modified.ShouldBe(s_modified);

        dirEntry.Type.ShouldBe(FileTreeEntryType.Dir);
        dirEntry.Created.ShouldBeNull();
        dirEntry.Modified.ShouldBeNull();
    }

    [Test]
    public async Task SerializeForStorage_WithPlaintext_ThenDeserialize_RoundTrips()
    {
        var enc  = new PlaintextPassthroughService();
        var blob = MakeBlob();

        var bytes = await FileTreeBlobSerializer.SerializeForStorageAsync(blob, enc);
        var back  = await FileTreeBlobSerializer.DeserializeFromStorageAsync(new MemoryStream(bytes), enc);

        back.Entries.Count.ShouldBe(2);
        var fileEntry = back.Entries.Single(e => e.Name == "photo.jpg");
        var dirEntry = back.Entries.Single(e => e.Name == "subdir/");

        fileEntry.Hash.ShouldBe("a1b2c3d4");
        fileEntry.Type.ShouldBe(FileTreeEntryType.File);
        fileEntry.Created.ShouldBe(s_created);
        fileEntry.Modified.ShouldBe(s_modified);

        dirEntry.Type.ShouldBe(FileTreeEntryType.Dir);
        dirEntry.Created.ShouldBeNull();
        dirEntry.Modified.ShouldBeNull();
    }

    [Test]
    public async Task SerializeForStorage_WithPassphrase_StartsWithArGcm1Magic()
    {
        var enc   = new PassphraseEncryptionService("test-passphrase");
        var blob  = MakeBlob();

        var bytes = await FileTreeBlobSerializer.SerializeForStorageAsync(blob, enc);

        var prefix = System.Text.Encoding.ASCII.GetString(bytes[..6]);
        prefix.ShouldBe("ArGCM1");
    }

    [Test]
    public async Task SerializeForStorage_WithPassphrase_OutputIsNotPlaintext()
    {
        var enc  = new PassphraseEncryptionService("test-passphrase");
        var blob = MakeBlob();

        var bytes = await FileTreeBlobSerializer.SerializeForStorageAsync(blob, enc);
        var text  = System.Text.Encoding.UTF8.GetString(bytes);

        text.ShouldNotContain("photo.jpg");
        text.ShouldNotContain("subdir/");
    }
}
