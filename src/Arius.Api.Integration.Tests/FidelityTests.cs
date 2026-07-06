using Arius.Api.Integration.Tests.Harness;
using Arius.Api.Testing;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.RestoreCommand;

namespace Arius.Api.Integration.Tests;

public class FidelityTests
{
    [Test]
    public async Task Canonical_archive_obeys_core_event_ordering()
    {
        var events = CanonicalScenarios.RepresentativeArchive().Events;

        // ScanComplete must precede any per-file/upload event (Core enumerates before it hashes/uploads).
        var scanIdx = IndexOf<ScanCompleteEvent>(events);
        var firstFileIdx = IndexOfAny(events, e => e is FileScannedEvent or FileHashingEvent or ChunkUploadedEvent);
        await Assert.That(scanIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(scanIdx).IsLessThan(firstFileIdx);
    }

    [Test]
    public async Task Canonical_restore_resolves_chunks_before_reporting_rehydration_and_only_prompts_when_archive_tier()
    {
        var scenario = CanonicalScenarios.RehydratingRestore();
        var pre = scenario.PreCostEvents;

        var resolveIdx = IndexOf<ChunkResolutionCompleteEvent>(pre);
        var rehydIdx = IndexOf<RehydrationStatusEvent>(pre);
        await Assert.That(resolveIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(resolveIdx).IsLessThan(rehydIdx);

        // A cost prompt is present iff the pre-cost classification reported archive-tier chunks.
        var status = (RehydrationStatusEvent)pre[rehydIdx];
        await Assert.That(scenario.CostPrompt is not null).IsEqualTo(status.NeedsRehydration > 0);
    }

    private static int IndexOf<T>(IReadOnlyList<Mediator.INotification> events)
        => IndexOfAny(events, e => e is T);

    private static int IndexOfAny(IReadOnlyList<Mediator.INotification> events, Func<Mediator.INotification, bool> pred)
    {
        for (var i = 0; i < events.Count; i++) if (pred(events[i])) return i;
        return -1;
    }
}
