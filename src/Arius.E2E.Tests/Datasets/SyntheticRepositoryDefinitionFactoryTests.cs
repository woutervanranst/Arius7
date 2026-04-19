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
        changeContent.TargetPath.ShouldBeNull();

        var delete = definition.V2Mutations.Single(x => x.Kind == SyntheticMutationKind.Delete);
        delete.Path.ShouldBe("docs/batch-00/doc-0000.txt");
        v1Paths.Contains(delete.Path).ShouldBeTrue();
        delete.TargetPath.ShouldBeNull();
        delete.ReplacementContentId.ShouldBeNull();

        var rename = definition.V2Mutations.Single(x => x.Kind == SyntheticMutationKind.Rename);
        rename.Path.ShouldBe("archives/duplicates/copy-a.bin");
        v1Paths.Contains(rename.Path).ShouldBeTrue();
        rename.TargetPath.ShouldBe("archives/duplicates/copy-a-renamed.bin");
        rename.ReplacementContentId.ShouldBeNull();

        var add = definition.V2Mutations.Single(x => x.Kind == SyntheticMutationKind.Add);
        add.Path.ShouldBe("src/module-99/group-00/new-file-0000.bin");
        v1Paths.Contains(add.Path).ShouldBeFalse();
        add.TargetPath.ShouldBeNull();
        add.ReplacementContentId.ShouldBe("new-000");
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

        var add = definition.V2Mutations.Single(x => x.Kind == SyntheticMutationKind.Add);
        add.Path.ShouldBe("src/simple/c.bin");
        v1Paths.Contains(add.Path).ShouldBeFalse();
        add.TargetPath.ShouldBeNull();
        add.ReplacementContentId.ShouldBe("small-004");
    }
}
