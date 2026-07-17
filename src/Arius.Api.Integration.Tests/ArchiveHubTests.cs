using System.Text.Json.Serialization;
using Arius.Api.FakeTestHost;
using Arius.Api.Integration.Tests.Harness;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Hashes;
using Microsoft.AspNetCore.SignalR.Client;

namespace Arius.Api.Integration.Tests;

public class ArchiveHubTests
{
    [Test]
    public async Task StartArchive_over_the_hub_completes_and_attach_returns_terminal_state()
    {
        await using var factory = new AriusApiFactory();
        var srcDir = Path.Combine(Path.GetTempPath(), $"arius-itest-src-{Guid.NewGuid():N}");
        Directory.CreateDirectory(srcDir);
        var repoId = factory.SeedRepository(localPath: srcDir);

        factory.Scenarios.SetArchive(repoId, new ArchiveScenario(
            Events:
            [
                new ScanCompleteEvent(1, 2000),
                new FileScannedEvent(RelativePath.Parse("a"), 2000),
                new ChunkUploadedEvent(ChunkHash.Parse(new string('c', 64)), 400, 2000),
            ],
            Result: new ArchiveResult
            {
                Success = true, FilesScanned = 1, EntriesExcluded = 0, FilesUploaded = 1, FilesDeduped = 0,
                OriginalSize = 2000, IncrementalSize = 2000, IncrementalStoredSize = 400, FastHashReused = 0,
                FastHashRehashed = 1, RootHash = null, SnapshotTime = DateTimeOffset.UnixEpoch,
            }));

        var handler = factory.Server.CreateHandler();
        await using var conn = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/arius", o =>
            {
                o.HttpMessageHandlerFactory = _ => handler;
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
            })
            .Build();

        // JobSink.Done sends a SINGLE argument — an anonymous object { jobId, status, summary, outcome } —
        // not four positional args (see src/Arius.Api/Jobs/JobSink.cs `Group?.SendAsync("Done", new {...})`).
        // The hub's JSON protocol is configured with camelCase property naming (Program.cs `AddJsonProtocol`),
        // hence the explicit JsonPropertyName mapping below (belt-and-braces vs. relying on the client's
        // default case-insensitive matching).
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.On<DoneMessage>("Done", msg => done.TrySetResult(msg.Status));

        await conn.StartAsync();
        var jobId = await conn.InvokeAsync<string>("StartArchive", repoId, "Archive", false, false, false);

        var terminal = await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(terminal).IsEqualTo("completed");
        await Assert.That(jobId).IsNotNull();
    }

    /// <summary>Mirrors the anonymous object <see cref="Arius.Api.Jobs.JobSink.Done"/> sends over the hub.</summary>
    private sealed record DoneMessage(
        [property: JsonPropertyName("jobId")] string? JobId,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("summary")] string Summary,
        [property: JsonPropertyName("outcome")] string? Outcome);
}
