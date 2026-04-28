using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Tests.Shared.FileTree;

public class FileTreeBlobSerializerTests
{
    private static readonly DateTimeOffset s_created  = new(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_modified = new(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly PlaintextPassthroughService s_enc = new();

    private static FileTreeBlob MakeBlob(params (string name, bool isDirectory, string hash)[] items) =>
        new()
        {
            Entries = BuildEntries(items)
        };

    private static string NormalizeHash(string hash)
        => hash.Length == 64 ? hash : hash[0].ToString().PadRight(64, char.ToLowerInvariant(hash[0]));

    private static IReadOnlyList<FileTreeEntry> BuildEntries((string name, bool isDirectory, string hash)[] items)
    {
        var entries = new List<FileTreeEntry>(items.Length);
        entries.AddRange(items.Select(item => (FileTreeEntry)(item.isDirectory
            ? new DirectoryEntry { Name = item.name, FileTreeHash = FileTreeHash.Parse(NormalizeHash(item.hash)) }
            : new FileEntry { Name      = item.name, ContentHash  = ContentHash.Parse(NormalizeHash(item.hash)), Created = s_created, Modified = s_modified })));

        return entries;
    }

    [Test]
    public void Deserialize_ProducesTypedFileAndDirectoryEntries()
    {
        var text = string.Join(
            '\n',
            [
                $"{new string('a', 64)} F {s_created:O} {s_modified:O} photo.jpg",
                $"{new string('b', 64)} D photos/"
            ]);

        var blob = FileTreeBlobSerializer.Deserialize(System.Text.Encoding.UTF8.GetBytes(text + "\n"));

        blob.Entries[0].ShouldBeOfType<FileEntry>();
        blob.Entries[1].ShouldBeOfType<DirectoryEntry>();
        ((FileEntry)blob.Entries[0]).ContentHash.ShouldBe(ContentHash.Parse(new string('a', 64)));
        ((DirectoryEntry)blob.Entries[1]).FileTreeHash.ShouldBe(FileTreeHash.Parse(new string('b', 64)));
    }

    [Test]
    public void Deserialize_SkipsEntriesWithInvalidHashes_AndPreservesValidEntries()
    {
        var text = string.Join(
            '\n',
            [
                $"not-a-hash F {s_created:O} {s_modified:O} broken.txt",
                $"{new string('a', 64)} F {s_created:O} {s_modified:O} healthy.txt",
                $"{new string('b', 64)} D photos/"
            ]);

        var blob = FileTreeBlobSerializer.Deserialize(System.Text.Encoding.UTF8.GetBytes(text + "\n"));

        blob.Entries.Count.ShouldBe(2);
        blob.Entries.Select(entry => entry.Name).ShouldBe(["healthy.txt", "photos/"]);
    }

    [Test]
    public void Deserialize_SkipsMalformedEntries_AndPreservesValidSiblings()
    {
        var text = string.Join(
            '\n',
            [
                $"not-a-hash F garbage broken.txt",
                $"{new string('c', 64)} D",
                $"{new string('a', 64)} F {s_created:O} {s_modified:O} healthy.txt",
                $"{new string('b', 64)} D photos/"
            ]);

        var blob = FileTreeBlobSerializer.Deserialize(System.Text.Encoding.UTF8.GetBytes(text + "\n"));

        blob.Entries.Count.ShouldBe(2);
        blob.Entries.Select(entry => entry.Name).ShouldBe(["healthy.txt", "photos/"]);
    }

    [Test]
    public void Deserialize_SkipsFileEntriesWithEmptyOrWhitespaceNames_AndPreservesValidSiblings()
    {
        var text = string.Join(
            '\n',
            [
                $"{new string('a', 64)} F {s_created:O} {s_modified:O} ",
                $"{new string('b', 64)} F {s_created:O} {s_modified:O}    ",
                $"{new string('c', 64)} F {s_created:O} {s_modified:O} healthy.txt",
                $"{new string('d', 64)} D photos/"
            ]);

        var blob = FileTreeBlobSerializer.Deserialize(System.Text.Encoding.UTF8.GetBytes(text + "\n"));

        blob.Entries.Count.ShouldBe(2);
        blob.Entries.Select(entry => entry.Name).ShouldBe(["healthy.txt", "photos/"]);
    }

    [Test]
    public async Task Serialize_ThenDeserialize_RoundTrips()
    {
        var blob = MakeBlob(
            ("photo.jpg", false, FakeContentHash('a').ToString()),
            ("subdir/",   true,  FakeFileTreeHash('e').ToString()));

        var bytes = await FileTreeBlobSerializer.SerializeForStorageAsync(blob, s_enc);
        var back  = await FileTreeBlobSerializer.DeserializeFromStorageAsync(new MemoryStream(bytes), s_enc);

        back.Entries.Count.ShouldBe(2);

        var file = back.Entries.Single(e => e.Name == "photo.jpg").ShouldBeOfType<FileEntry>();
        file.ContentHash.ShouldBe(FakeContentHash('a'));
        file.Created.ShouldNotBe(default);
        file.Modified.ShouldNotBe(default);

        var dir = back.Entries.Single(e => e.Name == "subdir/").ShouldBeOfType<DirectoryEntry>();
        dir.FileTreeHash.ShouldBe(FakeFileTreeHash('e'));
    }

    [Test]
    public async Task Serialize_SortsEntriesByName()
    {
        var blob = MakeBlob(
            ("z_last.txt",  false, FakeContentHash('3').ToString()),
            ("a_first.txt", false, FakeContentHash('1').ToString()),
            ("m_mid.txt",   false, FakeContentHash('2').ToString()));

        var bytes = await FileTreeBlobSerializer.SerializeForStorageAsync(blob, s_enc);
        var back  = await FileTreeBlobSerializer.DeserializeFromStorageAsync(new MemoryStream(bytes), s_enc);

        back.Entries[0].Name.ShouldBe("a_first.txt");
        back.Entries[1].Name.ShouldBe("m_mid.txt");
        back.Entries[2].Name.ShouldBe("z_last.txt");
    }

    [Test]
    public void Serialize_IsDeterministic_SameInputSameOutput()
    {
        var blob1 = MakeBlob(("b.jpg", false, FakeContentHash('2').ToString()), ("a.jpg", false, FakeContentHash('1').ToString()));
        var blob2 = MakeBlob(("a.jpg", false, FakeContentHash('1').ToString()), ("b.jpg", false, FakeContentHash('2').ToString()));

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
                new DirectoryEntry { Name = "sub/", FileTreeHash = FileTreeHash.Parse(NormalizeHash("abc")) },
                new FileEntry { Name = "f.txt", ContentHash = ContentHash.Parse(NormalizeHash("def")), Created = s_created, Modified = s_modified }
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
        var blob = MakeBlob(("my vacation photo.jpg", false, FakeContentHash('a').ToString()));

        var bytes = await FileTreeBlobSerializer.SerializeForStorageAsync(blob, s_enc);
        var back  = await FileTreeBlobSerializer.DeserializeFromStorageAsync(new MemoryStream(bytes), s_enc);

        back.Entries.Single().Name.ShouldBe("my vacation photo.jpg");
    }

    [Test]
    public async Task Serialize_DirEntryWithSpacesInName_RoundTrips()
    {
        var blob = MakeBlob(("2024 trip/", true, FakeFileTreeHash('d').ToString()));

        var bytes = await FileTreeBlobSerializer.SerializeForStorageAsync(blob, s_enc);
        var back  = await FileTreeBlobSerializer.DeserializeFromStorageAsync(new MemoryStream(bytes), s_enc);

        back.Entries.Single().Name.ShouldBe("2024 trip/");
    }

    [Test]
    public void ComputeHash_Deterministic_SameInputSameHash()
    {
        var enc  = new PlaintextPassthroughService();
        var blob = MakeBlob(("file.txt", false, FakeContentHash('d').ToString()));

        var h1 = FileTreeBlobSerializer.ComputeHash(blob, enc);
        var h2 = FileTreeBlobSerializer.ComputeHash(blob, enc);

        h1.ShouldBe(h2);
        h1.ShouldBeOfType<FileTreeHash>();
    }

    [Test]
    public void ComputeHash_MetadataChange_ProducesNewHash()
    {
        var enc  = new PlaintextPassthroughService();
        var blob1 = new FileTreeBlob
        {
            Entries =
            [
                new FileEntry
                {
                    Name     = "file.txt",
                    ContentHash = FakeContentHash('d'),
                    Created  = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    Modified = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
                }
            ]
        };
        var blob2 = blob1 with
        {
            Entries =
            [
                ((FileEntry)blob1.Entries[0]) with
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
        var blobPlain   = MakeBlob(("file.txt", false, FakeContentHash('a').ToString()));
        var plain       = new PlaintextPassthroughService();
        var withPass    = new PassphraseEncryptionService("secret");

        var h1 = FileTreeBlobSerializer.ComputeHash(blobPlain, plain);
        var h2 = FileTreeBlobSerializer.ComputeHash(blobPlain, withPass);

        h1.ShouldNotBe(h2);
    }
}
