using Arius.Api.Hubs;
using Arius.Api.Jobs;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Hashes;

namespace Arius.Api.Tests.Jobs;

/// <summary>
/// Locks down the forwarder wiring: hashed bytes are credited on completion (<see cref="FileHashedEvent"/>),
/// not at hash start (<see cref="FileHashingEvent"/>), and the exact new-byte upload total arrives via
/// <see cref="RoutingCompleteEvent"/>.
/// </summary>
public class ArchiveForwardersHashedRoutingTests
{
    private static ContentHash Hash(char c) => ContentHash.Parse(new string(c, 64));

    [Test]
    public async Task FileHashedForwarder_credits_hashed_bytes_on_completion()
    {
        var s = new JobSink();
        await new FileHashedForwarder(s).Handle(
            new FileHashedEvent(RelativePath.Parse("a.bin"), Hash('a'), FastHashReused: false, FastHashRehashed: true, FileSize: 4096), default);

        await Assert.That(s.BuildSnapshot(DateTimeOffset.UnixEpoch).HashedBytes).IsEqualTo(4096L);
    }

    [Test]
    public async Task FileHashingForwarder_advances_phase_but_does_not_credit_hashed_bytes()
    {
        // Crediting at hash start would make the hash counter — and the hash rate and ETA — lead reality by
        // the files still in flight.
        var s = new JobSink();
        await new FileHashingForwarder(s).Handle(new FileHashingEvent(RelativePath.Parse("a.bin"), 4096), default);

        var snap = s.BuildSnapshot(DateTimeOffset.UnixEpoch);
        await Assert.That(snap.HashedBytes).IsEqualTo(0L);
        await Assert.That(snap.Phase).IsEqualTo("hash-route");
    }

    [Test]
    public async Task RoutingCompleteForwarder_fixes_the_exact_new_byte_total()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var s  = new JobSink();
        s.SetTotals(files: 1, bytes: 1_000_000_000);
        s.AddQueuedNew(1_000_000);
        s.SampleForEta(t0);
        s.AddUploaded(ChunkHash.Parse(new string('a', 64)), stored: 0, original: 1_000_000);   // 1 MB/s
        s.SampleForEta(t0.AddSeconds(1));

        await Assert.That(s.BuildSnapshot(t0.AddSeconds(1)).EtaIsUpperBound).IsTrue();   // provisional pre-routing

        await new RoutingCompleteForwarder(s).Handle(new RoutingCompleteEvent(2_000_000), default);

        var snap = s.BuildSnapshot(t0.AddSeconds(1));
        await Assert.That(snap.EtaSeconds!.Value).IsBetween(1, 2);   // exact: (2M − 1M) / 1 MB/s ≈ 1 s
        await Assert.That(snap.EtaIsUpperBound).IsFalse();
    }
}
