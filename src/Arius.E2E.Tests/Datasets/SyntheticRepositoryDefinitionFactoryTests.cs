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
}
