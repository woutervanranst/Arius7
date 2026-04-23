using Arius.Core.Shared.Storage;
using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Fixtures;
using Arius.E2E.Tests.Workflows.Steps;

namespace Arius.E2E.Tests.Workflows;

public class RepresentativeWorkflowRunnerTests
{
    [Test]
    public async Task RunAsync_WhenResetCacheStepRuns_RecreatesFixtureThroughDependencyFactory()
    {
        var context = new E2EStorageBackendContext
        {
            BlobContainer = new NoOpBlobContainerService(),
            AccountName = "test-account",
            ContainerName = $"test-container-{Guid.NewGuid():N}",
            Capabilities = new E2EBackendCapabilities(
                SupportsArchiveTier: false,
                SupportsRehydrationPlanning: false),
            CleanupAsync = static () => ValueTask.CompletedTask,
        };
        var backend = new TestBackend(context);
        var createdFixtures = new List<E2EFixture>();
        var observedFixtures = new List<E2EFixture>();
        var workflow = new RepresentativeWorkflowDefinition(
            "reset-cache-recreates-fixture",
            SyntheticRepositoryProfile.Small,
            Seed: 123,
            Steps:
            [
                new CaptureFixtureStep(observedFixtures.Add),
                new ResetCacheStep(),
                new CaptureFixtureStep(observedFixtures.Add),
            ]);
        var dependencies = new RepresentativeWorkflowRunnerDependencies
        {
            CreateFixtureAsync = async (backendContext, cancellationToken) =>
            {
                var fixture = await E2EFixture.CreateAsync(
                    backendContext.BlobContainer,
                    backendContext.AccountName,
                    backendContext.ContainerName,
                    BlobTier.Cool,
                    ct: cancellationToken);
                createdFixtures.Add(fixture);
                return fixture;
            },
        };

        await RepresentativeWorkflowRunner.RunAsync(backend, workflow, dependencies);

        createdFixtures.Count.ShouldBe(2);
        observedFixtures.Count.ShouldBe(2);
        ReferenceEquals(observedFixtures[0], createdFixtures[0]).ShouldBeTrue();
        ReferenceEquals(observedFixtures[1], createdFixtures[1]).ShouldBeTrue();
        ReferenceEquals(observedFixtures[0], observedFixtures[1]).ShouldBeFalse();
        Directory.Exists(createdFixtures[0].LocalRoot).ShouldBeFalse();
        Directory.Exists(createdFixtures[1].LocalRoot).ShouldBeFalse();
    }

    private sealed class TestBackend(E2EStorageBackendContext context) : IE2EStorageBackend
    {
        public string Name => "test";

        public E2EBackendCapabilities Capabilities => context.Capabilities;

        public Task InitializeAsync() => Task.CompletedTask;

        public Task<E2EStorageBackendContext> CreateContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(context);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record CaptureFixtureStep(Action<E2EFixture> Capture, string Name = "capture-fixture") : IRepresentativeWorkflowStep
    {
        public Task ExecuteAsync(RepresentativeWorkflowState state, CancellationToken cancellationToken)
        {
            Capture(state.Fixture);
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpBlobContainerService : IBlobContainerService
    {
        public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UploadAsync(string blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<Stream> OpenWriteAsync(string blobName, string? contentType = null, CancellationToken cancellationToken = default)
            => Task.FromResult<Stream>(new MemoryStream());

        public Task<Stream> DownloadAsync(string blobName, CancellationToken cancellationToken = default)
            => Task.FromResult<Stream>(new MemoryStream());

        public Task<BlobMetadata> GetMetadataAsync(string blobName, CancellationToken cancellationToken = default)
            => Task.FromResult(new BlobMetadata { Exists = false });

        public async IAsyncEnumerable<string> ListAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task SetMetadataAsync(string blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SetTierAsync(string blobName, BlobTier tier, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task CopyAsync(string sourceBlobName, string destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeleteAsync(string blobName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
