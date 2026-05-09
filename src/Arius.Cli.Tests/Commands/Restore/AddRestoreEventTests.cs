namespace Arius.Cli.Tests.Commands.Restore;

/// <summary>
/// Verifies <see cref="ProgressState.AddRestoreEvent"/> caps the queue at 10 entries.
/// </summary>
public class AddRestoreEventTests
{
    [Test]
    public void AddRestoreEvent_CapAt10_KeepsMostRecent()
    {
        var state = new ProgressState();

        for (var i = 1; i <= 15; i++)
            state.AddRestoreEvent(RelativePath.Parse($"file{i}.txt"), i * 100L, skipped: false);

        state.RecentRestoreEvents.Count.ShouldBe(10);

        var paths = state.RecentRestoreEvents.Select(e => e.RelativePath).ToList();
        paths.ShouldContain(RelativePath.Parse("file15.txt"));
        paths.ShouldContain(RelativePath.Parse("file6.txt"));
        paths.ShouldNotContain(RelativePath.Parse("file5.txt"));
        paths.ShouldNotContain(RelativePath.Parse("file1.txt"));
    }

    [Test]
    public void AddRestoreEvent_BelowCap_AllRetained()
    {
        var state = new ProgressState();

        for (var i = 1; i <= 5; i++)
            state.AddRestoreEvent(RelativePath.Parse($"file{i}.txt"), 100L, skipped: i % 2 == 0);

        state.RecentRestoreEvents.Count.ShouldBe(5);
    }
}
