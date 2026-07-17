using Arius.Api.Jobs;
using Shouldly;

namespace Arius.Api.Tests.Jobs;

public sealed class RehydrationScheduleTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    [Test]
    public async Task High_priority_is_due_every_15_minutes()
    {
        RehydrationSchedule.IsDue(T0.AddMinutes(10), T0, T0, "High", firstChunkSeen: false).ShouldBeFalse();
        RehydrationSchedule.IsDue(T0.AddMinutes(15), T0, T0, "High", firstChunkSeen: false).ShouldBeTrue();
    }

    [Test]
    public async Task Standard_priority_waits_ten_hours_then_hourly()
    {
        RehydrationSchedule.IsDue(T0.AddHours(9), T0, T0, "Standard", firstChunkSeen: false).ShouldBeFalse();
        RehydrationSchedule.IsDue(T0.AddHours(10), T0, T0, "Standard", firstChunkSeen: false).ShouldBeTrue();
        // after a re-run at +10h, next due is +1h later
        RehydrationSchedule.IsDue(T0.AddHours(10).AddMinutes(30), T0, T0.AddHours(10), "Standard", false).ShouldBeFalse();
        RehydrationSchedule.IsDue(T0.AddHours(11), T0, T0.AddHours(10), "Standard", false).ShouldBeTrue();
    }

    [Test]
    public async Task Once_a_chunk_is_seen_cadence_tightens_to_15_minutes_regardless_of_priority()
    {
        RehydrationSchedule.IsDue(T0.AddMinutes(15), T0, T0, "Standard", firstChunkSeen: true).ShouldBeTrue();
        RehydrationSchedule.IsDue(T0.AddMinutes(10), T0, T0, "Standard", firstChunkSeen: true).ShouldBeFalse();
    }
}
