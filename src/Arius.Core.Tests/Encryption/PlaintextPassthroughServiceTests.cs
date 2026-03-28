using Arius.Core.Encryption;
using Shouldly;
using System.Security.Cryptography;

namespace Arius.Core.Tests.Encryption;

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
        var expected = SHA256.HashData(data);
        _svc.ComputeHash(data).ShouldBe(expected);
    }

    [Test]
    public async Task ComputeHashAsync_MatchesPlainSha256()
    {
        var data     = "streaming content"u8.ToArray();
        var expected = SHA256.HashData(data);
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
        var expected = SHA256.HashData([]);
        _svc.ComputeHash([]).ShouldBe(expected);
    }

    // ── CI reporter visibility probes ─────────────────────────────────────────

    [Test]
    [Skip("Always skipped - testing CI reporter visibility")]
    public void CiProbe_AlwaysSkipped()
    {
    }

}
