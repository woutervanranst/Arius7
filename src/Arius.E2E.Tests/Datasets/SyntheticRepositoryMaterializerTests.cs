namespace Arius.E2E.Tests.Datasets;

public class SyntheticRepositoryMaterializerTests
{
    [Test]
    public async Task Materialize_V1_Twice_WithSameSeed_ProducesSameTree()
    {
        var definition = SyntheticRepositoryDefinitionFactory.Create(
            SyntheticRepositoryProfile.Small);

        var leftRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var rightRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var left = await SyntheticRepositoryMaterializer.MaterializeAsync(
                definition,
                SyntheticRepositoryVersion.V1,
                seed: 12345,
                leftRoot);
            var right = await SyntheticRepositoryMaterializer.MaterializeAsync(
                definition,
                SyntheticRepositoryVersion.V1,
                seed: 12345,
                rightRoot);

            left.Files.ShouldBe(right.Files);
        }
        finally
        {
            if (Directory.Exists(leftRoot))
                Directory.Delete(leftRoot, recursive: true);

            if (Directory.Exists(rightRoot))
                Directory.Delete(rightRoot, recursive: true);
        }
    }

    [Test]
    public async Task Materialize_V2_AppliesConfiguredMutations()
    {
        var definition = SyntheticRepositoryDefinitionFactory.Create(
            SyntheticRepositoryProfile.Small);

        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var v1Root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var snapshot = await SyntheticRepositoryMaterializer.MaterializeAsync(
                definition,
                SyntheticRepositoryVersion.V2,
                seed: 12345,
                root);

            snapshot.Files.Keys.ShouldContain("src/simple/c.bin");
            snapshot.Files.Keys.ShouldContain("docs/readme.txt");

            var v1 = await SyntheticRepositoryMaterializer.MaterializeAsync(
                definition,
                SyntheticRepositoryVersion.V1,
                12345,
                v1Root);

            snapshot.Files["docs/readme.txt"].ShouldNotBe(v1.Files["docs/readme.txt"]);
            snapshot.Files["src/simple/c.bin"].ShouldNotBeNullOrWhiteSpace();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);

            if (Directory.Exists(v1Root))
                Directory.Delete(v1Root, recursive: true);
        }
    }
}
