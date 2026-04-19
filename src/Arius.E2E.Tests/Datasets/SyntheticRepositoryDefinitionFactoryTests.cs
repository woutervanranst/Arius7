namespace Arius.E2E.Tests.Datasets;

public class SyntheticRepositoryDefinitionFactoryTests
{
    [Test]
    public async Task Representative_Profile_ContainsExpectedMix()
    {
        await Task.CompletedTask;

        var definition = SyntheticRepositoryDefinitionFactory.Create(
            SyntheticRepositoryProfile.Representative);

        definition.RootDirectories.ShouldContain("docs");
        definition.RootDirectories.ShouldContain("media");
        definition.RootDirectories.ShouldContain("src");

        definition.Files.Count.ShouldBeGreaterThan(1000);
        definition.Files.Any(x => x.SizeBytes < definition.SmallFileThresholdBytes).ShouldBeTrue();
        definition.Files.Any(x => x.SizeBytes > definition.SmallFileThresholdBytes).ShouldBeTrue();
        definition.Files.Count(x => x.ContentId is not null).ShouldBeGreaterThan(0);
        definition.Files.Select(x => x.Path).Distinct().Count().ShouldBe(definition.Files.Count);
    }

    [Test]
    public async Task Representative_Profile_HasFixedShape()
    {
        await Task.CompletedTask;

        var definition = SyntheticRepositoryDefinitionFactory.Create(
            SyntheticRepositoryProfile.Representative);

        definition.SmallFileThresholdBytes.ShouldBe(256 * 1024);
        definition.RootDirectories.ShouldBe(["docs", "media", "src", "archives", "nested"]);
        definition.Files.Count.ShouldBe(1985);
        definition.Files.Count(x => x.Path.StartsWith("src/", StringComparison.Ordinal)).ShouldBe(1600);
        definition.Files.Count(x => x.Path.StartsWith("docs/", StringComparison.Ordinal)).ShouldBe(380);
        definition.Files.Count(x => x.Path.StartsWith("media/", StringComparison.Ordinal)).ShouldBe(2);
        definition.Files.Count(x => x.Path.StartsWith("archives/", StringComparison.Ordinal)).ShouldBe(1);
        definition.Files.Count(x => x.Path.StartsWith("nested/", StringComparison.Ordinal)).ShouldBe(2);

        definition.Files.Count(x => x.SizeBytes < definition.SmallFileThresholdBytes).ShouldBe(1980);
        definition.Files.Count(x => x.SizeBytes > definition.SmallFileThresholdBytes).ShouldBe(5);
        definition.Files.Count(x => x.ContentId == "dup-001").ShouldBe(3);
        definition.Files.Single(x => x.Path == "media/video/master-a.bin").SizeBytes.ShouldBe(48 * 1024 * 1024);
        definition.Files.Single(x => x.Path == "media/video/master-b.bin").SizeBytes.ShouldBe(72 * 1024 * 1024);
        definition.Files.Single(x => x.Path == "archives/duplicates/copy-a.bin").SizeBytes.ShouldBe(512 * 1024);
        definition.Files.Single(x => x.Path == "nested/deep/a/b/c/d/e/f/copy-b.bin").ContentId.ShouldBe("dup-001");
        definition.Files.Single(x => x.Path == "nested/deep/a/b/c/d/e/f/g/h/copy-c.bin").ContentId.ShouldBe("dup-001");
    }

    [Test]
    public async Task Representative_Profile_Defines_V2_MixedChanges()
    {
        await Task.CompletedTask;

        var definition = SyntheticRepositoryDefinitionFactory.Create(
            SyntheticRepositoryProfile.Representative);

        definition.V2Mutations.Any(x => x.Kind == SyntheticMutationKind.Add).ShouldBeTrue();
        definition.V2Mutations.Any(x => x.Kind == SyntheticMutationKind.Delete).ShouldBeTrue();
        definition.V2Mutations.Any(x => x.Kind == SyntheticMutationKind.Rename).ShouldBeTrue();
        definition.V2Mutations.Any(x => x.Kind == SyntheticMutationKind.ChangeContent).ShouldBeTrue();
    }

    [Test]
    public async Task Representative_Profile_Defines_Precise_V2_MutationContract()
    {
        await Task.CompletedTask;

        var definition = SyntheticRepositoryDefinitionFactory.Create(
            SyntheticRepositoryProfile.Representative);
        var v1Paths = definition.Files.Select(x => x.Path).ToHashSet(StringComparer.Ordinal);

        definition.V2Mutations.Count.ShouldBe(4);

        var changeContent = definition.V2Mutations.Single(x => x.Kind == SyntheticMutationKind.ChangeContent);
        changeContent.Path.ShouldBe("src/module-00/group-00/file-0000.bin");
        v1Paths.Contains(changeContent.Path).ShouldBeTrue();
        changeContent.ReplacementContentId.ShouldBe("small-updated-000");
        changeContent.ReplacementSizeBytes.ShouldBe(4 * 1024);
        changeContent.TargetPath.ShouldBeNull();

        var delete = definition.V2Mutations.Single(x => x.Kind == SyntheticMutationKind.Delete);
        delete.Path.ShouldBe("docs/batch-00/doc-0000.txt");
        v1Paths.Contains(delete.Path).ShouldBeTrue();
        delete.TargetPath.ShouldBeNull();
        delete.ReplacementContentId.ShouldBeNull();
        delete.ReplacementSizeBytes.ShouldBeNull();

        var rename = definition.V2Mutations.Single(x => x.Kind == SyntheticMutationKind.Rename);
        rename.Path.ShouldBe("archives/duplicates/copy-a.bin");
        v1Paths.Contains(rename.Path).ShouldBeTrue();
        rename.TargetPath.ShouldBe("archives/duplicates/copy-a-renamed.bin");
        rename.ReplacementContentId.ShouldBeNull();
        rename.ReplacementSizeBytes.ShouldBeNull();

        var add = definition.V2Mutations.Single(x => x.Kind == SyntheticMutationKind.Add);
        add.Path.ShouldBe("src/module-99/group-00/new-file-0000.bin");
        v1Paths.Contains(add.Path).ShouldBeFalse();
        add.TargetPath.ShouldBeNull();
        add.ReplacementContentId.ShouldBe("new-000");
        add.ReplacementSizeBytes.ShouldBe(24 * 1024);
    }

    [Test]
    public async Task Small_Profile_HasFixedShape_And_V2MutationContract()
    {
        await Task.CompletedTask;

        var definition = SyntheticRepositoryDefinitionFactory.Create(
            SyntheticRepositoryProfile.Small);
        var v1Paths = definition.Files.Select(x => x.Path).ToHashSet(StringComparer.Ordinal);

        definition.SmallFileThresholdBytes.ShouldBe(256 * 1024);
        definition.RootDirectories.ShouldBe(["docs", "media", "src"]);
        definition.Files.Count.ShouldBe(4);
        definition.Files.Select(x => x.Path).ShouldBe([
            "src/simple/a.bin",
            "src/simple/b.bin",
            "docs/readme.txt",
            "media/large.bin",
        ]);

        definition.Files.Count(x => x.SizeBytes < definition.SmallFileThresholdBytes).ShouldBe(3);
        definition.Files.Count(x => x.SizeBytes > definition.SmallFileThresholdBytes).ShouldBe(1);
        definition.Files.Count(x => x.ContentId == "small-001").ShouldBe(2);
        definition.Files.Single(x => x.Path == "media/large.bin").SizeBytes.ShouldBe(2 * 1024 * 1024);

        definition.V2Mutations.Count.ShouldBe(2);

        var changeContent = definition.V2Mutations.Single(x => x.Kind == SyntheticMutationKind.ChangeContent);
        changeContent.Path.ShouldBe("docs/readme.txt");
        v1Paths.Contains(changeContent.Path).ShouldBeTrue();
        changeContent.TargetPath.ShouldBeNull();
        changeContent.ReplacementContentId.ShouldBe("small-003");
        changeContent.ReplacementSizeBytes.ShouldBe(32 * 1024);

        var add = definition.V2Mutations.Single(x => x.Kind == SyntheticMutationKind.Add);
        add.Path.ShouldBe("src/simple/c.bin");
        v1Paths.Contains(add.Path).ShouldBeFalse();
        add.TargetPath.ShouldBeNull();
        add.ReplacementContentId.ShouldBe("small-004");
        add.ReplacementSizeBytes.ShouldBe(8 * 1024);
    }

    [Test]
    public async Task SyntheticMutation_Rejects_Invalid_State_Combinations()
    {
        await Task.CompletedTask;

        Should.Throw<ArgumentException>(() => new SyntheticMutation(
            SyntheticMutationKind.Rename,
            "docs/readme.txt"));

        Should.Throw<ArgumentException>(() => new SyntheticMutation(
            SyntheticMutationKind.Delete,
            "docs/readme.txt",
            ReplacementContentId: "ignored",
            ReplacementSizeBytes: 32 * 1024));

        Should.Throw<ArgumentException>(() => new SyntheticMutation(
            SyntheticMutationKind.Add,
            "src/new.bin",
            ReplacementContentId: "new-001"));

        Should.Throw<ArgumentException>(() => new SyntheticMutation(
            SyntheticMutationKind.ChangeContent,
            "src/file.bin",
            ReplacementSizeBytes: 8 * 1024));

        Should.Throw<ArgumentOutOfRangeException>(() => new SyntheticMutation(
            SyntheticMutationKind.Add,
            "src/new.bin",
            ReplacementContentId: "new-001",
            ReplacementSizeBytes: 0));
    }

    [Test]
    public async Task SyntheticMutation_Allows_Valid_State_Combinations()
    {
        await Task.CompletedTask;

        var add = new SyntheticMutation(
            SyntheticMutationKind.Add,
            "src/new.bin",
            ReplacementContentId: "new-001",
            ReplacementSizeBytes: 8 * 1024);

        var rename = new SyntheticMutation(
            SyntheticMutationKind.Rename,
            "src/old.bin",
            TargetPath: "src/new.bin");

        add.ReplacementSizeBytes.ShouldBe(8 * 1024);
        rename.TargetPath.ShouldBe("src/new.bin");
    }

    [Test]
    public async Task SyntheticFileDefinition_Rejects_Invalid_Values()
    {
        await Task.CompletedTask;

        Should.Throw<ArgumentException>(() => new SyntheticFileDefinition(
            "",
            8 * 1024,
            "small-001"));

        Should.Throw<ArgumentOutOfRangeException>(() => new SyntheticFileDefinition(
            "docs/readme.txt",
            0,
            "small-001"));

        Should.Throw<ArgumentException>(() => new SyntheticFileDefinition(
            "docs/readme.txt",
            8 * 1024,
            ""));
    }

    [Test]
    public async Task SyntheticRepositoryDefinition_Copies_Mutable_Input_Collections()
    {
        await Task.CompletedTask;

        var rootDirectories = new List<string> { "docs" };
        var files = new List<SyntheticFileDefinition>
        {
            new("docs/readme.txt", 8 * 1024, "small-001"),
        };
        var mutations = new List<SyntheticMutation>
        {
            new(SyntheticMutationKind.ChangeContent, "docs/readme.txt", ReplacementContentId: "small-002", ReplacementSizeBytes: 8 * 1024),
        };

        var definition = new SyntheticRepositoryDefinition(
            256 * 1024,
            rootDirectories,
            files,
            mutations);

        rootDirectories.Add("src");
        files.Add(new SyntheticFileDefinition("src/new.bin", 8 * 1024, "small-003"));
        mutations.Add(new SyntheticMutation(SyntheticMutationKind.Add, "src/new.bin", ReplacementContentId: "small-004", ReplacementSizeBytes: 8 * 1024));

        definition.RootDirectories.ShouldBe(["docs"]);
        definition.Files.Select(x => x.Path).ShouldBe(["docs/readme.txt"]);
        definition.V2Mutations.Count.ShouldBe(1);
        (definition.RootDirectories is string[]).ShouldBeFalse();
        (definition.Files is SyntheticFileDefinition[]).ShouldBeFalse();
        (definition.V2Mutations is SyntheticMutation[]).ShouldBeFalse();
    }

    [Test]
    public async Task SyntheticRepositoryDefinition_Rejects_Invalid_V2_Transitions()
    {
        await Task.CompletedTask;

        var files = new[]
        {
            new SyntheticFileDefinition("docs/readme.txt", 8 * 1024, "small-001"),
            new SyntheticFileDefinition("src/existing.bin", 8 * 1024, "small-002"),
        };

        Should.Throw<ArgumentException>(() => new SyntheticRepositoryDefinition(
            256 * 1024,
            ["docs", "src"],
            files,
            [new SyntheticMutation(SyntheticMutationKind.Delete, "docs/missing.txt")]));

        Should.Throw<ArgumentException>(() => new SyntheticRepositoryDefinition(
            256 * 1024,
            ["docs", "src"],
            files,
            [new SyntheticMutation(SyntheticMutationKind.ChangeContent, "docs/missing.txt", ReplacementContentId: "small-003", ReplacementSizeBytes: 8 * 1024)]));

        Should.Throw<ArgumentException>(() => new SyntheticRepositoryDefinition(
            256 * 1024,
            ["docs", "src"],
            files,
            [new SyntheticMutation(SyntheticMutationKind.Rename, "docs/readme.txt", TargetPath: "docs/readme.txt")]));

        Should.Throw<ArgumentException>(() => new SyntheticRepositoryDefinition(
            256 * 1024,
            ["docs", "src"],
            files,
            [new SyntheticMutation(SyntheticMutationKind.Rename, "docs/readme.txt", TargetPath: "src/existing.bin")]));

        Should.Throw<ArgumentException>(() => new SyntheticRepositoryDefinition(
            256 * 1024,
            ["docs", "src"],
            files,
            [new SyntheticMutation(SyntheticMutationKind.Add, "src/existing.bin", ReplacementContentId: "small-003", ReplacementSizeBytes: 8 * 1024)]));

        Should.Throw<ArgumentException>(() => new SyntheticRepositoryDefinition(
            256 * 1024,
            ["docs", "src"],
            files,
            [
                new SyntheticMutation(SyntheticMutationKind.Rename, "docs/readme.txt", TargetPath: "tmp/renamed.txt"),
                new SyntheticMutation(SyntheticMutationKind.Add, "tmp/renamed.txt", ReplacementContentId: "small-003", ReplacementSizeBytes: 8 * 1024),
            ]));
    }
}
