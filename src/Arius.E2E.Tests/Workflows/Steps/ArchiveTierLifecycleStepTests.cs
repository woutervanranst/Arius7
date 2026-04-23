using Arius.Core.Shared.Storage;
using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Fixtures;

namespace Arius.E2E.Tests.Workflows.Steps;

internal sealed class ArchiveTierLifecycleStepTests
{
    [Test]
    public async Task ExecuteAsync_Should_NoOp_When_ArchiveTier_Is_Unsupported()
    {
        var createFixtureCalled = false;
        var step = new ArchiveTierLifecycleStep("archive-tier");
        var state = new RepresentativeWorkflowState
        {
            Context = new E2EStorageBackendContext
            {
                BlobContainer = new ThrowingBlobContainerService(),
                AccountName = "test-account",
                ContainerName = "test-container",
                AzureBlobContainerService = null,
                Capabilities = new E2EBackendCapabilities(SupportsArchiveTier: false, SupportsRehydrationPlanning: false),
                CleanupAsync = () => ValueTask.CompletedTask,
            },
            CreateFixtureAsync = (_, _) =>
            {
                createFixtureCalled = true;
                throw new InvalidOperationException("CreateFixtureAsync should not be called for unsupported archive tier backends.");
            },
            Fixture = null!,
            Definition = new SyntheticRepositoryDefinition(
                RootDirectories: ["src"],
                Files: [new SyntheticFileDefinition("src/file.bin", 1, "content-1")],
                V2Mutations: []),
            Seed = 123,
            CurrentSourceVersion = SyntheticRepositoryVersion.V1,
        };

        await step.ExecuteAsync(state, CancellationToken.None);

        createFixtureCalled.ShouldBeFalse();
        state.ArchiveTierOutcome.ShouldBeNull();
    }

    private sealed class ThrowingBlobContainerService : IBlobContainerService
    {
        public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) => throw UnexpectedCall();

        public Task UploadAsync(string blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default) => throw UnexpectedCall();

        public Task<Stream> OpenWriteAsync(string blobName, string? contentType = null, CancellationToken cancellationToken = default) => throw UnexpectedCall();

        public Task<Stream> DownloadAsync(string blobName, CancellationToken cancellationToken = default) => throw UnexpectedCall();

        public Task<BlobMetadata> GetMetadataAsync(string blobName, CancellationToken cancellationToken = default) => throw UnexpectedCall();

        public IAsyncEnumerable<string> ListAsync(string prefix, CancellationToken cancellationToken = default) => throw UnexpectedCall();

        public Task SetMetadataAsync(string blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) => throw UnexpectedCall();

        public Task SetTierAsync(string blobName, BlobTier tier, CancellationToken cancellationToken = default) => throw UnexpectedCall();

        public Task CopyAsync(string sourceBlobName, string destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default) => throw UnexpectedCall();

        public Task DeleteAsync(string blobName, CancellationToken cancellationToken = default) => throw UnexpectedCall();

        static InvalidOperationException UnexpectedCall() => new("ArchiveTierLifecycleStep should no-op before touching blob storage when archive tier is unsupported.");
    }
}
