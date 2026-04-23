namespace Arius.E2E.Tests.Datasets;

internal enum SyntheticRepositoryProfile
{
    Small,
    Representative,
}

internal static class SyntheticRepositoryDefinitionFactory
{
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
                new SyntheticMutation(SyntheticMutationKind.ChangeContent, "docs/readme.txt", ReplacementContentId: "small-003", ReplacementSizeBytes: 32 * 1024),
                new SyntheticMutation(SyntheticMutationKind.Add, "src/simple/c.bin", ReplacementContentId: "small-004", ReplacementSizeBytes: 8 * 1024),
            ]);
    }

    static SyntheticRepositoryDefinition CreateRepresentative()
    {
        var files = new List<SyntheticFileDefinition>();

        for (var i = 0; i < 1600; i++)
        {
            files.Add(new SyntheticFileDefinition(
                $"src/module-{i % 40:D2}/group-{i % 7:D2}/file-{i:D4}.bin",
                4 * 1024 + (i % 16) * 1024,
                $"small-{i % 220:D3}"));
        }

        for (var i = 0; i < 380; i++)
        {
            files.Add(new SyntheticFileDefinition(
                $"docs/batch-{i % 12:D2}/doc-{i:D4}.txt",
                180 * 1024 + (i % 8) * 4096,
                $"edge-{i % 90:D3}"));
        }

        files.Add(new SyntheticFileDefinition("media/video/master-a.bin", 48 * 1024 * 1024, "large-001"));
        files.Add(new SyntheticFileDefinition("media/video/master-b.bin", 72 * 1024 * 1024, "large-002"));
        files.Add(new SyntheticFileDefinition("archives/duplicates/copy-a.bin", 512 * 1024, "dup-001"));

        files.Add(new SyntheticFileDefinition("archives/duplicates/copy-a.bin",         512 * 1024, "dup-small-001"));
        files.Add(new SyntheticFileDefinition("nested/deep/a/b/c/d/e/f/copy-b.bin",     512 * 1024, "dup-small-001"));
        files.Add(new SyntheticFileDefinition("nested/deep/a/b/c/d/e/f/g/h/copy-c.bin", 512 * 1024, "dup-small-001"));

        files.Add(new SyntheticFileDefinition("archives/duplicates/binary-a.bin", 2 * 1024 * 1024, "dup-large-001"));
        files.Add(new SyntheticFileDefinition("nested/deep/a/b/c/binary-b.bin",   2 * 1024 * 1024, "dup-large-001"));

        IReadOnlyList<SyntheticMutation> mutations =
        [
            new(SyntheticMutationKind.ChangeContent, "src/module-00/group-00/file-0000.bin", ReplacementContentId: "small-updated-000", ReplacementSizeBytes: 4 * 1024),
            new(SyntheticMutationKind.Delete, "docs/batch-00/doc-0000.txt"),
            new(SyntheticMutationKind.Rename, "archives/duplicates/copy-a.bin", TargetPath: "archives/duplicates/copy-a-renamed.bin"),
            new(SyntheticMutationKind.Add, "src/module-99/group-00/new-file-0000.bin", ReplacementContentId: "new-000", ReplacementSizeBytes: 24 * 1024),
        ];

        return new SyntheticRepositoryDefinition(
            ["docs", "media", "src", "archives", "nested"],
            files,
            mutations);
    }
}
