using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Tests.Shared.FileTree;

public class FileTreeSerializerTests
{
    private static readonly DateTimeOffset s_created  = new(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_modified = new(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly PlaintextPassthroughService s_enc = new();

    private static IReadOnlyList<FileTreeEntry> MakeEntries(params (string name, bool isDirectory, string hash)[] items)
        => BuildEntries(items);

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
    public void Deserialize_InvalidFileHash_Throws()
    {
        var text = string.Join(
            '\n',
            [
                $"not-a-hash F {s_created:O} {s_modified:O} broken.txt",
                $"{new string('a', 64)} F {s_created:O} {s_modified:O} healthy.txt",
                $"{new string('b', 64)} D photos/"
            ]);

        Should.Throw<FormatException>(() => FileTreeSerializer.Deserialize(System.Text.Encoding.UTF8.GetBytes(text + "\n")));
    }

    [Test]
    public void Deserialize_MalformedEntries_Throw()
    {
        var text = string.Join(
            '\n',
            [
                $"not-a-hash F garbage broken.txt",
                $"{new string('c', 64)} D",
                $"{new string('a', 64)} F {s_created:O} {s_modified:O} healthy.txt",
                $"{new string('b', 64)} D photos/"
            ]);

        Should.Throw<FormatException>(() => FileTreeSerializer.Deserialize(System.Text.Encoding.UTF8.GetBytes(text + "\n")));
    }

    [Test]
    public void Deserialize_EmptyOrWhitespaceFileNames_Throw()
    {
        var text = string.Join(
            '\n',
            [
                $"{new string('a', 64)} F {s_created:O} {s_modified:O} ",
                $"{new string('b', 64)} F {s_created:O} {s_modified:O}    ",
                $"{new string('c', 64)} F {s_created:O} {s_modified:O} healthy.txt",
                $"{new string('d', 64)} D photos/"
            ]);

        Should.Throw<FormatException>(() => FileTreeSerializer.Deserialize(System.Text.Encoding.UTF8.GetBytes(text + "\n")));
    }

    [Test]
    public async Task Serialize_ThenDeserialize_RoundTrips()
    {
        var entries = MakeEntries(
            ("photo.jpg", false, FakeContentHash('a').ToString()),
            ("subdir/",   true,  FakeFileTreeHash('e').ToString()));

        var bytes = await FileTreeSerializer.SerializeForStorageAsync(entries, s_enc);
        var back  = await FileTreeSerializer.DeserializeFromStorageAsync(new MemoryStream(bytes), s_enc);

        back.Count.ShouldBe(2);

        var file = back.Single(e => e.Name == "photo.jpg").ShouldBeOfType<FileEntry>();
        file.ContentHash.ShouldBe(FakeContentHash('a'));
        file.Created.ShouldNotBe(default);
        file.Modified.ShouldNotBe(default);

        var dir = back.Single(e => e.Name == "subdir/").ShouldBeOfType<DirectoryEntry>();
        dir.FileTreeHash.ShouldBe(FakeFileTreeHash('e'));
    }

    [Test]
    public async Task Serialize_SortsEntriesByName()
    {
        var entries = MakeEntries(
            ("z_last.txt",  false, FakeContentHash('3').ToString()),
            ("a_first.txt", false, FakeContentHash('1').ToString()),
            ("m_mid.txt",   false, FakeContentHash('2').ToString()));

        var bytes = await FileTreeSerializer.SerializeForStorageAsync(entries, s_enc);
        var back  = await FileTreeSerializer.DeserializeFromStorageAsync(new MemoryStream(bytes), s_enc);

        back[0].Name.ShouldBe("a_first.txt");
        back[1].Name.ShouldBe("m_mid.txt");
        back[2].Name.ShouldBe("z_last.txt");
    }

    [Test]
    public void Serialize_IsDeterministic_ForEquivalentEntryLists()
    {
        var entries1 = MakeEntries(("b.jpg", false, FakeContentHash('2').ToString()), ("a.jpg", false, FakeContentHash('1').ToString()));
        var entries2 = MakeEntries(("a.jpg", false, FakeContentHash('1').ToString()), ("b.jpg", false, FakeContentHash('2').ToString()));

        var bytes1 = FileTreeSerializer.Serialize(entries1);
        var bytes2 = FileTreeSerializer.Serialize(entries2);

        bytes1.ShouldBe(bytes2);
    }

    [Test]
    public void Serialize_DirEntry_HasNoTimestamps_FileEntry_HasTimestamps()
    {
        IReadOnlyList<FileTreeEntry> entries =
        [
            new DirectoryEntry { Name = "sub/", FileTreeHash = FileTreeHash.Parse(NormalizeHash("abc")) },
            new FileEntry { Name = "f.txt", ContentHash = ContentHash.Parse(NormalizeHash("def")), Created = s_created, Modified = s_modified }
        ];

        var text = System.Text.Encoding.UTF8.GetString(FileTreeSerializer.Serialize(entries));
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var dirLine = lines.Single(l => l.Contains(" D "));
        dirLine.Split(' ').Length.ShouldBe(3);

        var fileLine = lines.Single(l => l.Contains(" F "));
        fileLine.Split(' ').Length.ShouldBe(5);
    }

    [Test]
    public async Task Serialize_FileEntryWithSpacesInName_RoundTrips()
    {
        var entries = MakeEntries(("my vacation photo.jpg", false, FakeContentHash('a').ToString()));

        var bytes = await FileTreeSerializer.SerializeForStorageAsync(entries, s_enc);
        var back  = await FileTreeSerializer.DeserializeFromStorageAsync(new MemoryStream(bytes), s_enc);

        back.Single().Name.ShouldBe("my vacation photo.jpg");
    }

    [Test]
    public async Task Serialize_DirEntryWithSpacesInName_RoundTrips()
    {
        var entries = MakeEntries(("2024 trip/", true, FakeFileTreeHash('d').ToString()));

        var bytes = await FileTreeSerializer.SerializeForStorageAsync(entries, s_enc);
        var back  = await FileTreeSerializer.DeserializeFromStorageAsync(new MemoryStream(bytes), s_enc);

        back.Single().Name.ShouldBe("2024 trip/");
    }

    [Test]
    public void SerializeFileEntryLine_RoundTripsSingleFileEntry()
    {
        var created = new DateTimeOffset(2026, 4, 29, 10, 0, 0, TimeSpan.Zero);
        var modified = created.AddMinutes(5);
        var entry = new FileEntry
        {
            Name = "file with spaces.txt",
            ContentHash = ContentHash.Parse(NormalizeHash("abc")),
            Created = created,
            Modified = modified
        };

        var line = FileTreeSerializer.SerializeFileEntryLine(entry);
        var parsed = FileTreeSerializer.ParseFileEntryLine(line);

        parsed.ShouldBe(entry);
    }

    [Test]
    public void ParseFileEntryLine_TrailingCarriageReturn_RoundTripsSingleFileEntry()
    {
        var created = new DateTimeOffset(2026, 4, 29, 10, 0, 0, TimeSpan.Zero);
        var modified = created.AddMinutes(5);
        var entry = new FileEntry
        {
            Name = "file with spaces.txt",
            ContentHash = ContentHash.Parse(NormalizeHash("abc")),
            Created = created,
            Modified = modified
        };

        var line = FileTreeSerializer.SerializeFileEntryLine(entry) + "\r";
        var parsed = FileTreeSerializer.ParseFileEntryLine(line);

        parsed.ShouldBe(entry);
    }

    [Test]
    public void ComputeHash_Deterministic_SameInputSameHash()
    {
        var enc  = new PlaintextPassthroughService();
        var entries = MakeEntries(("file.txt", false, FakeContentHash('d').ToString()));

        var h1 = FileTreeSerializer.ComputeHash(entries, enc);
        var h2 = FileTreeSerializer.ComputeHash(entries, enc);

        h1.ShouldBe(h2);
    }

    [Test]
    public void ComputeHash_MetadataChange_ProducesNewHash()
    {
        var enc  = new PlaintextPassthroughService();
        IReadOnlyList<FileTreeEntry> entries1 =
        [
            new FileEntry
            {
                Name     = "file.txt",
                ContentHash = FakeContentHash('d'),
                Created  = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
                Modified = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
            }
        ];
        IReadOnlyList<FileTreeEntry> entries2 =
        [
            ((FileEntry)entries1[0]) with
            {
                Modified = new DateTimeOffset(2024, 6, 15, 0, 0, 0, TimeSpan.Zero)
            }
        ];

        var h1 = FileTreeSerializer.ComputeHash(entries1, enc);
        var h2 = FileTreeSerializer.ComputeHash(entries2, enc);

        h1.ShouldNotBe(h2);
    }

    [Test]
    public void ComputeHash_WithPassphrase_DifferentFromPlaintext()
    {
        var entries = MakeEntries(("file.txt", false, FakeContentHash('a').ToString()));
        var plain       = new PlaintextPassthroughService();
        var withPass    = new PassphraseEncryptionService("secret");

        var h1 = FileTreeSerializer.ComputeHash(entries, plain);
        var h2 = FileTreeSerializer.ComputeHash(entries, withPass);

        h1.ShouldNotBe(h2);
    }
}
