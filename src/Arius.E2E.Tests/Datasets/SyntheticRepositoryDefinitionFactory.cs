namespace Arius.E2E.Tests.Datasets;

internal enum SyntheticRepositoryProfile
{
    Small,
    Representative,
}

internal static class SyntheticRepositoryDefinitionFactory
{
    public const int RepresentativeScaleDivisor = 8; // tweak this parameter to make the test data set larger or smaller. 1 = full representative dataset size

    public const string SmallDuplicateRenameSourcePath = "archives/duplicates/copy-a.bin";
    public const string SmallDuplicateStablePathA      = "nested/deep/a/b/c/d/e/f/copy-b.bin";
    public const string SmallDuplicateStablePathB      = "nested/deep/a/b/c/d/e/f/g/h/copy-c.bin";
    public const string SmallDuplicateRenameTargetPath = "archives/duplicates/copy-a-renamed.bin";

    public const string LargeDuplicatePathA = "archives/duplicates/binary-a.bin";
    public const string LargeDuplicatePathB = "nested/deep/a/b/c/binary-b.bin";

    public static SyntheticRepositoryDefinition Create(SyntheticRepositoryProfile profile)
    {
        return profile switch
        {
            SyntheticRepositoryProfile.Small          => CreateSmall(),
            SyntheticRepositoryProfile.Representative => CreateRepresentative(),
            _                                         => throw new ArgumentOutOfRangeException(nameof(profile)),
        };
    }

    static SyntheticRepositoryDefinition CreateSmall()
    {
        return new SyntheticRepositoryDefinition(
            ["docs", "media", "src"],
            [
                new SyntheticFileDefinition("src/simple/a.bin", 8 * 1024,  "small-001"),
                new SyntheticFileDefinition("src/simple/b.bin", 8 * 1024,  "small-001"),
                new SyntheticFileDefinition("docs/readme.txt",  32 * 1024, "small-002"),
                new SyntheticFileDefinition("media/large.bin",  2 * 1024 * 1024, "large-001"),
            ],
            [
                new SyntheticFileMutation(SyntheticFileMutationKind.ChangeContent, "docs/readme.txt", ReplacementContentId: "small-003", ReplacementSizeBytes: 32 * 1024),
                new SyntheticFileMutation(SyntheticFileMutationKind.Add, "src/simple/c.bin", ReplacementContentId: "small-004", ReplacementSizeBytes: 8 * 1024),
            ]);
    }

    static SyntheticRepositoryDefinition CreateRepresentative()
    {
        var files = new List<SyntheticFileDefinition>();

        for (var i = 0; i < 1600 / RepresentativeScaleDivisor; i++)
        {
            files.Add(new SyntheticFileDefinition(
                $"src/module-{i % 40:D2}/group-{i % 7:D2}/file-{i:D4}.bin",
                4 * 1024 + (i % 16) * 1024,
                $"small-{i % 220:D3}"));
        }

        for (var i = 0; i < 380 / RepresentativeScaleDivisor; i++)
        {
            files.Add(new SyntheticFileDefinition(
                $"docs/batch-{i % 12:D2}/doc-{i:D4}.txt",
                180 * 1024 + (i % 8) * 4096,
                $"edge-{i % 90:D3}"));
        }

        files.Add(new SyntheticFileDefinition("media/video/master-a.bin", 48 * 1024 * 1024 / RepresentativeScaleDivisor, "large-001"));
        files.Add(new SyntheticFileDefinition("media/video/master-b.bin", 72 * 1024 * 1024 / RepresentativeScaleDivisor, "large-002"));

        files.Add(new SyntheticFileDefinition(SmallDuplicateRenameSourcePath, 512 * 1024, "dup-small-001"));
        files.Add(new SyntheticFileDefinition(SmallDuplicateStablePathA,       512 * 1024, "dup-small-001"));
        files.Add(new SyntheticFileDefinition(SmallDuplicateStablePathB,       512 * 1024, "dup-small-001"));

        files.Add(new SyntheticFileDefinition(LargeDuplicatePathA, 2 * 1024 * 1024, "dup-large-001"));
        files.Add(new SyntheticFileDefinition(LargeDuplicatePathB, 2 * 1024 * 1024, "dup-large-001"));

        IReadOnlyList<SyntheticFileMutation> mutations =
        [
            new(SyntheticFileMutationKind.ChangeContent, "src/module-00/group-00/file-0000.bin", ReplacementContentId: "small-updated-000", ReplacementSizeBytes: 4 * 1024),
            new(SyntheticFileMutationKind.Delete, "docs/batch-00/doc-0000.txt"),
            new(SyntheticFileMutationKind.Rename, SmallDuplicateRenameSourcePath, TargetPath: SmallDuplicateRenameTargetPath),
            new(SyntheticFileMutationKind.Add, "src/module-00/group-00/new-file-0000.bin", ReplacementContentId: "new-000", ReplacementSizeBytes: 24 * 1024),
        ];

        return new SyntheticRepositoryDefinition(
            ["docs", "media", "src", "archives", "nested"],
            files,
            mutations);
    }
}
