namespace Arius.E2E.Tests.Datasets;

public class RepositoryTreeAssertionsTests
{
    [Test]
    public async Task AssertMatchesDiskTree_Succeeds_ForEquivalentTree()
    {
        var definition = SyntheticRepositoryDefinitionFactory.Create(
            SyntheticRepositoryProfile.Small);

        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var snapshot = await SyntheticRepositoryMaterializer.MaterializeAsync(
                definition,
                SyntheticRepositoryVersion.V1,
                seed: 12345,
                root);

            await RepositoryTreeAssertions.AssertMatchesDiskTreeAsync(snapshot, root);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
