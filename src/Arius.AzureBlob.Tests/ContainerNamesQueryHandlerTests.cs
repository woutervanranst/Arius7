using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Shouldly;

namespace Arius.AzureBlob.Tests;

public class ContainerNamesQueryHandlerTests
{
    [Test]
    public async Task Handle_YieldsOnlyContainersWithSnapshotsPrefix()
    {
        var serviceClient = new FakeBlobServiceClient(
            [
                new FakeContainer("repo-a", ["snapshots/2026-04-01T120000.000Z"]),
                new FakeContainer("not-a-repo", ["random/file.txt"]),
                new FakeContainer("repo-b", ["snapshots/2026-04-02T120000.000Z", "chunks/abc"]),
                new FakeContainer("empty", [])
            ]);

        var handler = new ContainerNamesQueryHandler();

        var results = new List<string>();
        await foreach (var name in handler.Handle(new ContainerNamesQuery(serviceClient), CancellationToken.None))
        {
            results.Add(name);
        }

        results.ShouldBe(["repo-a", "repo-b"]);
    }

    private sealed record FakeContainer(string Name, IReadOnlyList<string> BlobNames);

    private sealed class FakeBlobServiceClient(IReadOnlyList<FakeContainer> containers) : BlobServiceClient
    {
        public override AsyncPageable<BlobContainerItem> GetBlobContainersAsync(
            BlobContainerTraits traits = BlobContainerTraits.None,
            BlobContainerStates states = BlobContainerStates.None,
            string prefix = default!,
            CancellationToken cancellationToken = default)
        {
            var items = containers
                .Where(container => string.IsNullOrEmpty(prefix) || container.Name.StartsWith(prefix, StringComparison.Ordinal))
                .Select(container => BlobsModelFactory.BlobContainerItem(
                    container.Name,
                    BlobsModelFactory.BlobContainerProperties(
                        DateTimeOffset.UtcNow,
                        default,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        new Dictionary<string, string>())))
                .ToArray();

            return AsyncPageable<BlobContainerItem>.FromPages(
                [Page<BlobContainerItem>.FromValues(items, continuationToken: null, FakeResponse.Instance)]);
        }

        public override BlobContainerClient GetBlobContainerClient(string blobContainerName)
        {
            var container = containers.Single(container => container.Name == blobContainerName);
            return new FakeBlobContainerClient(container);
        }
    }

    private sealed class FakeBlobContainerClient(FakeContainer container) : BlobContainerClient
    {
        public override AsyncPageable<BlobItem> GetBlobsAsync(
            BlobTraits traits = BlobTraits.None,
            BlobStates states = BlobStates.None,
            string prefix = default!,
            CancellationToken cancellationToken = default)
        {
            var items = container.BlobNames
                .Where(name => string.IsNullOrEmpty(prefix) || name.StartsWith(prefix, StringComparison.Ordinal))
                .Select(name => BlobsModelFactory.BlobItem(
                    name,
                    false,
                    BlobsModelFactory.BlobItemProperties(
                        true,
                        new Uri("https://example.test/blob"),
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        1L,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        default,
                        null,
                        null,
                        null,
                        null),
                    null,
                    new Dictionary<string, string>()))
                .ToArray();

            return AsyncPageable<BlobItem>.FromPages(
                [Page<BlobItem>.FromValues(items, continuationToken: null, FakeResponse.Instance)]);
        }
    }

    private sealed class FakeResponse : Response
    {
        public static FakeResponse Instance { get; } = new();

        public override int Status => 200;
        public override string ReasonPhrase => "OK";
        public override Stream? ContentStream { get; set; }
        public override string ClientRequestId { get; set; } = string.Empty;

        public override void Dispose() { }

        protected override bool ContainsHeader(string name) => false;
        protected override IEnumerable<HttpHeader> EnumerateHeaders() => [];
        protected override bool TryGetHeader(string name, out string value)
        {
            value = string.Empty;
            return false;
        }

        protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values)
        {
            values = Array.Empty<string>();
            return false;
        }
    }
}
