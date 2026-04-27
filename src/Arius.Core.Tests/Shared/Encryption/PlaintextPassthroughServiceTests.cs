using System.Security.Cryptography;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Hashes;
using Arius.Core.Tests.Shared.Streaming;

namespace Arius.Core.Tests.Shared.Encryption;

public class PlaintextPassthroughServiceTests
{
    private readonly PlaintextPassthroughService _svc = new();

    // ── WrapForEncryption / WrapForDecryption are pass-through ────────────────

    [Test]
    public void WrapForEncryption_ReturnsSameStreamInstance()
    {
        var ms = new MemoryStream();
        _svc.WrapForEncryption(ms).ShouldBeSameAs(ms);
    }

    [Test]
    public void WrapForDecryption_ReturnsSameStreamInstance()
    {
        var ms = new MemoryStream();
        _svc.WrapForDecryption(ms).ShouldBeSameAs(ms);
    }

    // ── Plain SHA256 hashing ──────────────────────────────────────────────────

    [Test]
    public void ComputeHash_MatchesPlainSha256()
    {
        var data     = "plaintext content"u8.ToArray();
        var expected = ContentHash.FromDigest(SHA256.HashData(data));
        _svc.ComputeHash(data).ShouldBe(expected);
    }

    [Test]
    public async Task ComputeHashAsync_MatchesPlainSha256()
    {
        var data     = "streaming content"u8.ToArray();
        var expected = ContentHash.FromDigest(SHA256.HashData(data));
        var actual   = await _svc.ComputeHashAsync(new MemoryStream(data));
        actual.ShouldBe(expected);
    }

    [Test]
    public void ComputeHash_Deterministic()
    {
        var data = "same data"u8.ToArray();
        _svc.ComputeHash(data).ShouldBe(_svc.ComputeHash(data));
    }

    [Test]
    public void ComputeHash_EmptyArray_MatchesSha256Empty()
    {
        var expected = ContentHash.FromDigest(SHA256.HashData([]));
        _svc.ComputeHash([]).ShouldBe(expected);
    }

    [Test]
    public async Task ComputeHashAsync_FilePath_MatchesStreamVariant()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllBytesAsync(path, "streaming determinism"u8.ToArray());

        try
        {
            await using var stream = File.OpenRead(path);

            var fromPath   = await _svc.ComputeHashAsync(path);
            var fromStream = await _svc.ComputeHashAsync(stream);

            fromPath.ShouldBe(fromStream);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ComputeHashAsync_FilePath_ReportsProgress()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllBytesAsync(path, new byte[4096]);

        try
        {
            long reported = 0;
            var progress = new SyncProgress<long>(value => reported = value);

            _ = await _svc.ComputeHashAsync(path, progress);

            reported.ShouldBe(4096);
        }
        finally
        {
            File.Delete(path);
        }
    }


}
