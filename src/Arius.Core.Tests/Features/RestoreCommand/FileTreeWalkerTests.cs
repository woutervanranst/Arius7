using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.Compression;
using Arius.Core.Shared.FileTree;
using Arius.Core.Tests.Fakes;
using Arius.Tests.Shared;

namespace Arius.Core.Tests.Features.RestoreCommand;

public class FileTreeWalkerTests
{
    private static readonly DateTimeOffset s_created = new(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_modified = new(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task WalkFilesAsync_YieldsFilesBreadthFirst()
    {
        var nestedTree = Entries(FileEntryOf("deep.txt", FakeContentHash('d')));
        var childTree = Entries(
            DirectoryEntryOf("nested", await ComputeHashAsync(nestedTree)),
            FileEntryOf("child.txt", FakeContentHash('c')));
        var rootTree = Entries(
            DirectoryEntryOf("child", await ComputeHashAsync(childTree)),
            FileEntryOf("root-a.txt", FakeContentHash('a')),
            FileEntryOf("root-b.txt", FakeContentHash('b')));

        var walker = await CreateWalkerAsync(rootTree, childTree, nestedTree);

        var files = await walker.WalkFilesAsync(await ComputeHashAsync(rootTree), [], CancellationToken.None)
            .Select(file => file.RelativePath)
            .ToListAsync();

        files.ShouldBe([
            RelativePath.Parse("root-a.txt"),
            RelativePath.Parse("root-b.txt"),
            RelativePath.Parse("child/child.txt"),
            RelativePath.Parse("child/nested/deep.txt")
        ]);
    }

    [Test]
    public async Task WalkFilesAsync_TargetDirectory_YieldsOnlyDescendantFiles()
    {
        var photosTree = Entries(FileEntryOf("pic.jpg", FakeContentHash('1')));
        var photoshopTree = Entries(FileEntryOf("logo.png", FakeContentHash('2')));
        var rootTree = Entries(
            DirectoryEntryOf("photos", await ComputeHashAsync(photosTree)),
            DirectoryEntryOf("photoshop", await ComputeHashAsync(photoshopTree)),
            FileEntryOf("root.txt", FakeContentHash('3')));

        var walker = await CreateWalkerAsync(rootTree, photosTree, photoshopTree);

        var files = await walker.WalkFilesAsync(await ComputeHashAsync(rootTree), [RelativePath.Parse("photos")], CancellationToken.None)
            .Select(file => file.RelativePath)
            .ToListAsync();

        files.ShouldBe([RelativePath.Parse("photos/pic.jpg")]);
    }

    [Test]
    public async Task WalkFilesAsync_TargetFile_YieldsOnlyThatFile()
    {
        var photosTree = Entries(
            FileEntryOf("pic.jpg", FakeContentHash('1')),
            FileEntryOf("other.jpg", FakeContentHash('2')));
        var rootTree = Entries(DirectoryEntryOf("photos", await ComputeHashAsync(photosTree)));

        var walker = await CreateWalkerAsync(rootTree, photosTree);

        var files = await walker.WalkFilesAsync(await ComputeHashAsync(rootTree), [RelativePath.Parse("photos/pic.jpg")], CancellationToken.None)
            .ToListAsync();

        var file = files.Single();
        file.RelativePath.ShouldBe(RelativePath.Parse("photos/pic.jpg"));
        file.ContentHash.ShouldBe(FakeContentHash('1'));
        file.Created.ShouldBe(s_created);
        file.Modified.ShouldBe(s_modified);
    }

    [Test]
    public async Task WalkFilesAsync_MultipleTargets_YieldsEachSelectedFileOnce()
    {
        var photosTree = Entries(
            FileEntryOf("pic.jpg", FakeContentHash('1')),
            FileEntryOf("other.jpg", FakeContentHash('2')));
        var docsTree = Entries(
            FileEntryOf("a.txt", FakeContentHash('3')),
            FileEntryOf("b.txt", FakeContentHash('4')));
        var rootTree = Entries(
            DirectoryEntryOf("photos", await ComputeHashAsync(photosTree)),
            DirectoryEntryOf("docs", await ComputeHashAsync(docsTree)),
            FileEntryOf("root.txt", FakeContentHash('5')));

        var walker = await CreateWalkerAsync(rootTree, photosTree, docsTree);

        // "photos/pic.jpg" overlaps "photos": the file must still be yielded once.
        var files = await walker.WalkFilesAsync(
                await ComputeHashAsync(rootTree),
                [RelativePath.Parse("photos"), RelativePath.Parse("docs/a.txt"), RelativePath.Parse("photos/pic.jpg")],
                CancellationToken.None)
            .Select(file => file.RelativePath)
            .ToListAsync();

        files.ShouldBe([
            RelativePath.Parse("photos/pic.jpg"),
            RelativePath.Parse("photos/other.jpg"),
            RelativePath.Parse("docs/a.txt")
        ], ignoreOrder: true);
    }

    [Test]
    public async Task WalkFilesAsync_TargetDirectoryWithOnlyNestedFiles_YieldsThem()
    {
        var yearTree = Entries(FileEntryOf("pic.jpg", FakeContentHash('1')));
        var photosTree = Entries(DirectoryEntryOf("2024", await ComputeHashAsync(yearTree)));
        var rootTree = Entries(
            DirectoryEntryOf("photos", await ComputeHashAsync(photosTree)),
            FileEntryOf("root.txt", FakeContentHash('2')));

        var walker = await CreateWalkerAsync(rootTree, photosTree, yearTree);

        var files = await walker.WalkFilesAsync(await ComputeHashAsync(rootTree), [RelativePath.Parse("photos")], CancellationToken.None)
            .Select(file => file.RelativePath)
            .ToListAsync();

        files.ShouldBe([RelativePath.Parse("photos/2024/pic.jpg")]);
    }

    [Test]
    public async Task WalkFilesAsync_TargetSelectingNoFile_ThrowsNamingOnlyThatTarget()
    {
        var photosTree = Entries(FileEntryOf("pic.jpg", FakeContentHash('1')));
        var rootTree = Entries(DirectoryEntryOf("photos", await ComputeHashAsync(photosTree)));

        var walker = await CreateWalkerAsync(rootTree, photosTree);

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await walker.WalkFilesAsync(
                    await ComputeHashAsync(rootTree),
                    [RelativePath.Parse("photos"), RelativePath.Parse("photos/pic.jpg"), RelativePath.Parse("photos/typo.jpg")],
                    CancellationToken.None)
                .ToListAsync());

        ex.Message.ShouldContain("1 target path(s) not found");
        ex.Message.ShouldContain("photos/typo.jpg");
    }

    private static async Task<FileTreeWalker> CreateWalkerAsync(params IReadOnlyList<FileTreeEntry>[] trees)
    {
        var blobs = new FakeSeededBlobContainerService();
        foreach (var tree in trees)
            await SeedTreeAsync(blobs, tree);

        return new FileTreeWalker(new FileTreeService(blobs, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance, "acct-filetree-walker", "ctr-filetree-walker"));
    }

    private static Task<FileTreeHash> ComputeHashAsync(IReadOnlyList<FileTreeEntry> entries)
        => Task.FromResult(FileTreeBuilder.ComputeHash(entries, IEncryptionService.PlaintextInstance));

    private static async Task SeedTreeAsync(FakeSeededBlobContainerService blobs, IReadOnlyList<FileTreeEntry> entries)
    {
        var plaintext = FileTreeSerializer.Serialize(entries);
        var payload = (Hash: FileTreeHashOf(plaintext, IEncryptionService.PlaintextInstance), Plaintext: (ReadOnlyMemory<byte>)plaintext);
        using var ms = new MemoryStream();

        await using (var encStream = IEncryptionService.PlaintextInstance.WrapForEncryption(ms))
        await using (var gzipStream = new System.IO.Compression.GZipStream(encStream, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
        {
            await gzipStream.WriteAsync(payload.Plaintext);
        }

        blobs.AddBlob(BlobPaths.FileTreePath(payload.Hash), ms.ToArray());
    }

    private static IReadOnlyList<FileTreeEntry> Entries(params FileTreeEntry[] entries) => entries;

    private static FileEntry FileEntryOf(string name, ContentHash hash) => new()
    {
        Name = PathSegment.Parse(name),
        ContentHash = hash,
        Created = s_created,
        Modified = s_modified
    };

    private static DirectoryEntry DirectoryEntryOf(string name, FileTreeHash hash) => new()
    {
        Name = PathSegment.Parse(name),
        FileTreeHash = hash
    };
}
