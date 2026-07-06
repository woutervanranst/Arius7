using System.Text.Json;
using Arius.Api.Jobs;
using Shouldly;

namespace Arius.Api.Tests.Jobs;

public sealed class PersistedJobStateTests
{
    [Test]
    public async Task Resume_params_round_trip_through_state_json()
    {
        var sink = new JobSink();
        sink.SetRestoreTotals(files: 5, bytes: 5000);
        var resume = new RestoreResumeState
        {
            Version = "v3", TargetPaths = new[] { "docs" }, Destination = "/data",
            Overwrite = false, NoPointers = true, Priority = "High", AutoResume = true,
            RehydrationStartedAt = DateTimeOffset.UnixEpoch, LastRunAt = DateTimeOffset.UnixEpoch,
            RehydrationWindow = TimeSpan.FromHours(1), RehydratedCount = 2,
        };

        var json = JsonSerializer.Serialize(sink.BuildPersistedState(DateTimeOffset.UnixEpoch, resume));
        var back = JsonSerializer.Deserialize<PersistedJobState>(json)!;

        back.Resume.ShouldNotBeNull();
        back.Resume!.Priority.ShouldBe("High");
        back.Resume.TargetPaths.ShouldBe(new[] { "docs" });
        back.Resume.RehydrationWindow.ShouldBe(TimeSpan.FromHours(1));
        back.Snapshot.RestoreTotalFiles.ShouldBe(5L);
    }
}
