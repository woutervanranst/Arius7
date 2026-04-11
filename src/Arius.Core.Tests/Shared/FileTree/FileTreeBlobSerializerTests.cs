using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Shouldly;

namespace Arius.Core.Tests.Shared.FileTree;

public class FileTreeBlobSerializerTests
{
    private static readonly DateTimeOffset s_created  = new(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_modified = new(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly PlaintextPassthroughService s_enc = new();

    private static FileTreeBlob MakeBlob(params (string name, FileTreeEntryType type, string hash)[] items) =>
        new()
        {
            Entries = items.Select(i => new FileTreeEntry
            {
                Name     = i.name,
                Type     = i.type,
                Hash     = i.hash,
                Created  = i.type == FileTreeEntryType.File ? s_created  : null,
                Modified = i.type == FileTreeEntryType.File ? s_modified : null
            }).ToList()
        };

    [Test]
    public async Task Serialize_ThenDeserialize_RoundTrips()
    {
        var blob = MakeBlob(
            ("photo.jpg", FileTreeEntryType.File, "a1b2c3d4"),
            ("subdir/",   FileTreeEntryType.Dir,  "e5f6a7b8"));

        var bytes = await FileTreeBlobSerializer.SerializeForStorageAsync(blob, s_enc);
        var back  = await FileTreeBlobSerializer.DeserializeFromStorageAsync(new MemoryStream(bytes), s_enc);

        back.Entries.Count.ShouldBe(2);

        var file = back.Entries.Single(e => e.Name == "photo.jpg");
        file.Type.ShouldBe(FileTreeEntryType.File);
        file.Hash.ShouldBe("a1b2c3d4");
        file.Created.ShouldNotBeNull();
        file.Modified.ShouldNotBeNull();

        var dir = back.Entries.Single(e => e.Name == "subdir/");
        dir.Type.ShouldBe(FileTreeEntryType.Dir);
        dir.Hash.ShouldBe("e5f6a7b8");
        dir.Created.ShouldBeNull();
        dir.Modified.ShouldBeNull();
    }

    [Test]
    public async Task Serialize_SortsEntriesByName()
    {
        var blob = MakeBlob(
            ("z_last.txt",  FileTreeEntryType.File, "hash3"),
            ("a_first.txt", FileTreeEntryType.File, "hash1"),
            ("m_mid.txt",   FileTreeEntryType.File, "hash2"));

        var bytes = await FileTreeBlobSerializer.SerializeForStorageAsync(blob, s_enc);
        var back  = await FileTreeBlobSerializer.DeserializeFromStorageAsync(new MemoryStream(bytes), s_enc);

        back.Entries[0].Name.ShouldBe("a_first.txt");
        back.Entries[1].Name.ShouldBe("m_mid.txt");
        back.Entries[2].Name.ShouldBe("z_last.txt");
    }

    [Test]
    public void Serialize_IsDeterministic_SameInputSameOutput()
    {
        var blob1 = MakeBlob(("b.jpg", FileTreeEntryType.File, "hash2"), ("a.jpg", FileTreeEntryType.File, "hash1"));
        var blob2 = MakeBlob(("a.jpg", FileTreeEntryType.File, "hash1"), ("b.jpg", FileTreeEntryType.File, "hash2"));

        var bytes1 = FileTreeBlobSerializer.Serialize(blob1);
        var bytes2 = FileTreeBlobSerializer.Serialize(blob2);

        bytes1.ShouldBe(bytes2);
    }

    [Test]
    public void Serialize_DirEntry_HasNoTimestamps_FileEntry_HasTimestamps()
    {
        var blob = new FileTreeBlob
        {
            Entries =
            [
                new FileTreeEntry { Name = "sub/", Type = FileTreeEntryType.Dir,  Hash = "abc", Created = null, Modified = null },
                new FileTreeEntry { Name = "f.txt", Type = FileTreeEntryType.File, Hash = "def", Created = s_created, Modified = s_modified }
            ]
        };

        var text = System.Text.Encoding.UTF8.GetString(FileTreeBlobSerializer.Serialize(blob));
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var dirLine = lines.Single(l => l.Contains(" D "));
        dirLine.Split(' ').Length.ShouldBe(3);

        var fileLine = lines.Single(l => l.Contains(" F "));
        fileLine.Split(' ').Length.ShouldBe(5);
    }

    [Test]
    public async Task Serialize_FileEntryWithSpacesInName_RoundTrips()
    {
        var blob = MakeBlob(("my vacation photo.jpg", FileTreeEntryType.File, "abc123"));

        var bytes = await FileTreeBlobSerializer.SerializeForStorageAsync(blob, s_enc);
        var back  = await FileTreeBlobSerializer.DeserializeFromStorageAsync(new MemoryStream(bytes), s_enc);

        back.Entries.Single().Name.ShouldBe("my vacation photo.jpg");
    }

    [Test]
    public async Task Serialize_DirEntryWithSpacesInName_RoundTrips()
    {
        var blob = MakeBlob(("2024 trip/", FileTreeEntryType.Dir, "def456"));

        var bytes = await FileTreeBlobSerializer.SerializeForStorageAsync(blob, s_enc);
        var back  = await FileTreeBlobSerializer.DeserializeFromStorageAsync(new MemoryStream(bytes), s_enc);

        back.Entries.Single().Name.ShouldBe("2024 trip/");
    }

    [Test]
    public void ComputeHash_Deterministic_SameInputSameHash()
    {
        var enc  = new PlaintextPassthroughService();
        var blob = MakeBlob(("file.txt", FileTreeEntryType.File, "deadbeef"));

        var h1 = FileTreeBlobSerializer.ComputeHash(blob, enc);
        var h2 = FileTreeBlobSerializer.ComputeHash(blob, enc);

        h1.ShouldBe(h2);
        h1.Length.ShouldBe(64);
    }

    [Test]
    public void ComputeHash_MetadataChange_ProducesNewHash()
    {
        var enc  = new PlaintextPassthroughService();
        var blob1 = new FileTreeBlob
        {
            Entries =
            [
                new FileTreeEntry
                {
                    Name     = "file.txt",
                    Type     = FileTreeEntryType.File,
                    Hash     = "deadbeef",
                    Created  = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    Modified = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
                }
            ]
        };
        var blob2 = blob1 with
        {
            Entries =
            [
                blob1.Entries[0] with
                {
                    Modified = new DateTimeOffset(2024, 6, 15, 0, 0, 0, TimeSpan.Zero)
                }
            ]
        };

        var h1 = FileTreeBlobSerializer.ComputeHash(blob1, enc);
        var h2 = FileTreeBlobSerializer.ComputeHash(blob2, enc);

        h1.ShouldNotBe(h2);
    }

    [Test]
    public void ComputeHash_WithPassphrase_DifferentFromPlaintext()
    {
        var blobPlain   = MakeBlob(("file.txt", FileTreeEntryType.File, "abc"));
        var plain       = new PlaintextPassthroughService();
        var withPass    = new PassphraseEncryptionService("secret");

        var h1 = FileTreeBlobSerializer.ComputeHash(blobPlain, plain);
        var h2 = FileTreeBlobSerializer.ComputeHash(blobPlain, withPass);

        h1.ShouldNotBe(h2);
    }
}
