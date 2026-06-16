using System.Security.Cryptography;
using Arius.Core.Shared.Streaming;
using Arius.Core.Tests.Shared.Streaming;

namespace Arius.Core.Tests.Shared.Encryption;

public class PlaintextPassthroughServiceTests
{
    // ── WrapForEncryption / WrapForDecryption are pass-through ────────────────

    [Test]
    public void WrapForEncryption_ReturnsSameStreamInstance()
    {
        var ms = new MemoryStream();
        TestEncryption.Instance.WrapForEncryption(ms).ShouldBeSameAs(ms);
    }

    [Test]
    public void WrapForDecryption_ReturnsSameStreamInstance()
    {
        var ms = new MemoryStream();
        TestEncryption.Instance.WrapForDecryption(ms).ShouldBeSameAs(ms);
    }

    // ── Plain SHA256 hashing ──────────────────────────────────────────────────

    [Test]
    public void ComputeHash_MatchesPlainSha256()
    {
        var data     = "plaintext content"u8.ToArray();
        var expected = ContentHash.FromDigest(SHA256.HashData(data));
        TestEncryption.Instance.ComputeHash(data).ShouldBe(expected);
    }

    [Test]
    public async Task ComputeHashAsync_FileStream_MatchesStreamVariant()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllBytesAsync(path, "streaming determinism"u8.ToArray());

        try
        {
            await using var firstStream = File.OpenRead(path);
            await using var secondStream = File.OpenRead(path);

            var firstHash = await TestEncryption.Instance.ComputeHashAsync(firstStream);
            var secondHash = await TestEncryption.Instance.ComputeHashAsync(secondStream);

            firstHash.ShouldBe(secondHash);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ComputeHashAsync_ProgressStream_ReportsProgress()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllBytesAsync(path, new byte[4096]);

        try
        {
            long reported = 0;
            var progress = new SyncProgress<long>(value => reported = value);
            await using var stream = File.OpenRead(path);
            await using var progressStream = new ProgressStream(stream, progress);

            _ = await TestEncryption.Instance.ComputeHashAsync(progressStream);

            reported.ShouldBe(4096);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
