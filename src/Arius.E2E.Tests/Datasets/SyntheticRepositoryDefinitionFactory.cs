namespace Arius.E2E.Tests.Datasets;

internal enum SyntheticRepositoryProfile
{
    Small,
    Representative,
}

internal static class SyntheticRepositoryDefinitionFactory
{
    public const int RepresentativeScaleDivisor = 8; // tweak this parameter to make the test data set larger or smaller. 8 = ~32 MB in 254 files. 1 = full representative dataset size

    public static readonly RelativePath SmallDuplicateRenameSourcePath = RelativePath.Parse("archives/duplicates/copy-a.bin");
    public static readonly RelativePath SmallDuplicateStablePathA      = RelativePath.Parse("nested/deep/a/b/c/d/e/f/copy-b.bin");
    public static readonly RelativePath SmallDuplicateStablePathB      = RelativePath.Parse("nested/deep/a/b/c/d/e/f/g/h/copy-c.bin");
    public static readonly RelativePath SmallDuplicateRenameTargetPath = RelativePath.Parse("archives/duplicates/copy-a-renamed.bin");

    public static readonly RelativePath LargeDuplicatePathA = RelativePath.Parse("archives/duplicates/binary-a.bin");
    public static readonly RelativePath LargeDuplicatePathB = RelativePath.Parse("nested/deep/a/b/c/binary-b.bin");
    public static readonly RelativePath UpdatedV1Path       = RelativePath.Parse("src/module-00/group-00/file-0000.bin");
    public static readonly RelativePath AddedV2Path         = RelativePath.Parse("src/module-00/group-00/new-file-0000.bin");
    public static readonly RelativePath DeletedV2Path       = RelativePath.Parse("docs/batch-00/doc-0000.txt");

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
            [RelativePath.Parse("docs"), RelativePath.Parse("media"), RelativePath.Parse("src")],
            [
                new SyntheticFileDefinition(RelativePath.Parse("src/simple/a.bin"), 8 * 1024,  "small-001"),
                new SyntheticFileDefinition(RelativePath.Parse("src/simple/b.bin"), 8 * 1024,  "small-001"),
                new SyntheticFileDefinition(RelativePath.Parse("docs/readme.txt"),  32 * 1024, "small-002"),
                new SyntheticFileDefinition(RelativePath.Parse("media/large.bin"),  2 * 1024 * 1024, "large-001"),
            ],
            [
                new SyntheticFileMutation(SyntheticFileMutationKind.ChangeContent, RelativePath.Parse("docs/readme.txt"), ReplacementContentId: "small-003", ReplacementSizeBytes: 32 * 1024),
                new SyntheticFileMutation(SyntheticFileMutationKind.Add, RelativePath.Parse("src/simple/c.bin"), ReplacementContentId: "small-004", ReplacementSizeBytes: 8 * 1024),
            ]);
    }

    static SyntheticRepositoryDefinition CreateRepresentative()
    {
        var files = new List<SyntheticFileDefinition>();

        for (var i = 0; i < 1600 / RepresentativeScaleDivisor; i++)
        {
            files.Add(new SyntheticFileDefinition(
                RelativePath.Parse($"src/module-{i % 40:D2}/group-{i % 7:D2}/file-{i:D4}.bin"),
                4 * 1024 + (i % 16) * 1024,
                $"small-{i % 220:D3}"));
        }

        for (var i = 0; i < 380 / RepresentativeScaleDivisor; i++)
        {
            files.Add(new SyntheticFileDefinition(
                RelativePath.Parse($"docs/batch-{i % 12:D2}/doc-{i:D4}.txt"),
                180 * 1024 + (i % 8) * 4096,
                $"edge-{i % 90:D3}"));
        }

        files.Add(new SyntheticFileDefinition(RelativePath.Parse("media/video/master-a.bin"), 48 * 1024 * 1024 / RepresentativeScaleDivisor, "large-001"));
        files.Add(new SyntheticFileDefinition(RelativePath.Parse("media/video/master-b.bin"), 72 * 1024 * 1024 / RepresentativeScaleDivisor, "large-002"));

        files.Add(new SyntheticFileDefinition(SmallDuplicateRenameSourcePath, 512 * 1024, "dup-small-001"));
        files.Add(new SyntheticFileDefinition(SmallDuplicateStablePathA,       512 * 1024, "dup-small-001"));
        files.Add(new SyntheticFileDefinition(SmallDuplicateStablePathB,       512 * 1024, "dup-small-001"));

        files.Add(new SyntheticFileDefinition(LargeDuplicatePathA, 2 * 1024 * 1024, "dup-large-001"));
        files.Add(new SyntheticFileDefinition(LargeDuplicatePathB, 2 * 1024 * 1024, "dup-large-001"));

        IReadOnlyList<SyntheticFileMutation> mutations =
        [
            new(SyntheticFileMutationKind.ChangeContent, UpdatedV1Path, ReplacementContentId: "small-updated-000", ReplacementSizeBytes: 4 * 1024),
            new(SyntheticFileMutationKind.Delete, DeletedV2Path),
            new(SyntheticFileMutationKind.Rename, SmallDuplicateRenameSourcePath, TargetPath: SmallDuplicateRenameTargetPath),
            new(SyntheticFileMutationKind.Add, AddedV2Path, ReplacementContentId: "new-000", ReplacementSizeBytes: 24 * 1024),
        ];

        return new SyntheticRepositoryDefinition(
            [RelativePath.Parse("docs"), RelativePath.Parse("media"), RelativePath.Parse("src"), RelativePath.Parse("archives"), RelativePath.Parse("nested")],
            files,
            mutations);
    }
}
