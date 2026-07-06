using Arius.Api.Jobs;
using Arius.Core.Shared.Storage;
using Shouldly;

namespace Arius.Api.Tests.Jobs;

public sealed class RestoreApprovalRegistryTests
{
    [Test]
    public async Task Resolve_completes_the_wait_with_the_chosen_priority()
    {
        var reg = new RestoreApprovalRegistry();
        var waiting = reg.RegisterAsync("j1", TimeSpan.FromSeconds(5), CancellationToken.None);

        reg.Resolve("j1", RehydratePriority.High);
        var result = await waiting;

        result.Approved.ShouldBeTrue();
        result.TimedOut.ShouldBeFalse();
        result.Priority.ShouldBe(RehydratePriority.High);
    }

    [Test]
    public async Task Resolve_null_is_a_decline()
    {
        var reg = new RestoreApprovalRegistry();
        var waiting = reg.RegisterAsync("j1", TimeSpan.FromSeconds(5), CancellationToken.None);

        reg.Resolve("j1", null);
        var result = await waiting;

        result.Approved.ShouldBeFalse();
        result.TimedOut.ShouldBeFalse();
    }

    [Test]
    public async Task Unanswered_wait_times_out()
    {
        var reg = new RestoreApprovalRegistry();
        var result = await reg.RegisterAsync("j1", TimeSpan.FromMilliseconds(50), CancellationToken.None);

        result.Approved.ShouldBeFalse();
        result.TimedOut.ShouldBeTrue();
    }

    [Test]
    public async Task Approve_before_timeout_is_honored()
    {
        var reg = new RestoreApprovalRegistry();
        var wait = reg.RegisterAsync("j", TimeSpan.FromSeconds(5), CancellationToken.None);
        while (!reg.HasPending("j")) await Task.Yield();
        reg.Resolve("j", RehydratePriority.High);
        var r = await wait;
        await Assert.That(r.Approved).IsTrue();
        await Assert.That(r.Priority).IsEqualTo(RehydratePriority.High);
        await Assert.That(r.TimedOut).IsFalse();
    }

    [Test]
    public async Task Timeout_with_no_answer_reports_timed_out()
    {
        var reg = new RestoreApprovalRegistry();
        var r = await reg.RegisterAsync("j", TimeSpan.FromMilliseconds(20), CancellationToken.None);
        await Assert.That(r.TimedOut).IsTrue();
        await Assert.That(r.Approved).IsFalse();
    }

    [Test]
    public async Task Approval_racing_the_deadline_is_never_silently_dropped()
    {
        // Stress the WhenAny/Resolve interleaving: a real approval must yield Approved, or a genuine timeout
        // must yield TimedOut — never a non-approved, non-timed-out result (which would be a lost approval).
        var sawApproved = false;
        for (var i = 0; i < 300; i++)
        {
            var reg = new RestoreApprovalRegistry();
            var wait = reg.RegisterAsync($"j{i}", TimeSpan.FromMilliseconds(1), CancellationToken.None);
            var resolve = Task.Run(() => reg.Resolve($"j{i}", RehydratePriority.High));
            await Task.WhenAll(wait, resolve);
            var r = await wait;
            // Invariant: if not timed out, it must be an honored approval (never a dropped one).
            if (!r.TimedOut) { await Assert.That(r.Approved).IsTrue(); await Assert.That(r.Priority).IsEqualTo(RehydratePriority.High); sawApproved = true; }
        }
        await Assert.That(sawApproved).IsTrue();   // the honor-path actually fires under contention
    }
}
