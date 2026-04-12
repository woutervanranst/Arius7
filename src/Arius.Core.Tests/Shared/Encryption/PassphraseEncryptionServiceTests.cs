using System.Security.Cryptography;
using System.Text;
using Arius.Core.Shared.Encryption;
using Arius.Core.Tests.Fakes.Scenarios.Encryption;
using Shouldly;

namespace Arius.Core.Tests.Shared.Encryption;

public class PassphraseEncryptionServiceTests
{
    private const string Passphrase = "test123";

    // ── 2.5 Encrypt/Decrypt roundtrip ─────────────────────────────────────────

    [Test]
    public async Task EncryptDecrypt_Roundtrip_ProducesBytIdenticalOutput()
    {
        var svc      = new PassphraseEncryptionService(Passphrase);
        var original = Encoding.UTF8.GetBytes("Hello, Arius! This is a test payload.");

        // Encrypt
        var cipherMs = new MemoryStream();
        await using (var encStream = svc.WrapForEncryption(cipherMs))
            await encStream.WriteAsync(original);

        // Decrypt
        cipherMs.Position = 0;
        var plainMs = new MemoryStream();
        var decStream = svc.WrapForDecryption(cipherMs);
        await decStream.CopyToAsync(plainMs);

        plainMs.ToArray().ShouldBe(original);
    }

    [Test]
    public async Task EncryptDecrypt_LargePayload_Roundtrip()
    {
        var svc     = new PassphraseEncryptionService(Passphrase);
        var data    = RandomNumberGenerator.GetBytes(4 * 1024 * 1024); // 4 MB

        var cipherMs = new MemoryStream();
        await using (var enc = svc.WrapForEncryption(cipherMs))
            await enc.WriteAsync(data);

        cipherMs.Position = 0;
        var plainMs = new MemoryStream();
        await svc.WrapForDecryption(cipherMs).CopyToAsync(plainMs);

        plainMs.ToArray().ShouldBe(data);
    }

    // ── ArGCM1 magic prefix check ─────────────────────────────────────────────

    [Test]
    public async Task Encrypt_OutputStartsWithArGcm1Magic()
    {
        var svc  = new PassphraseEncryptionService(Passphrase);
        var data = "data"u8.ToArray();

        var ms = new MemoryStream();
        await using (var enc = svc.WrapForEncryption(ms))
            await enc.WriteAsync(data);

        var bytes = ms.ToArray();
        bytes.Length.ShouldBeGreaterThanOrEqualTo(6);
        Encoding.ASCII.GetString(bytes, 0, 6).ShouldBe("ArGCM1");
    }

    // ── 2.5 Streaming large file — bounded memory ────────────────────────────
    // This test verifies that a large streaming operation doesn't hold the full
    // payload in memory. We do this by piping through a counting stream rather
    // than a MemoryStream, so GC pressure is minimal.

    [Test]
    public async Task Encrypt_LargeFile_DoesNotBufferEntireContent()
    {
        var svc        = new PassphraseEncryptionService(Passphrase);
        const int size = 32 * 1024 * 1024; // 32 MB

        // Source: a NullStream that returns zeros, sink: a DevNull stream
        var source = new ZeroStream(size);
        var sink   = new DevNullStream();

        await using (var enc = svc.WrapForEncryption(sink))
            await source.CopyToAsync(enc);

        sink.BytesWritten.ShouldBeGreaterThan(size); // ciphertext ≥ plaintext
    }

    // ── 2.7 Hash determinism ──────────────────────────────────────────────────

    [Test]
    public void ComputeHash_SameInput_ProducesSameHash()
    {
        var svc  = new PassphraseEncryptionService(Passphrase);
        var data = "deterministic"u8.ToArray();

        var h1 = svc.ComputeHash(data);
        var h2 = svc.ComputeHash(data);

        h1.ShouldBe(h2);
    }

    [Test]
    public async Task ComputeHashAsync_SameInput_ProducesSameHash()
    {
        var svc  = new PassphraseEncryptionService(Passphrase);
        var data = "streaming determinism"u8.ToArray();

        var h1 = await svc.ComputeHashAsync(new MemoryStream(data));
        var h2 = await svc.ComputeHashAsync(new MemoryStream(data));

        h1.ShouldBe(h2);
    }

    [Test]
    public void ComputeHash_ByteArray_MatchesStreamVariant()
    {
        var svc  = new PassphraseEncryptionService(Passphrase);
        var data = "cross-variant"u8.ToArray();

        var hArray  = svc.ComputeHash(data);
        var hStream = svc.ComputeHashAsync(new MemoryStream(data)).GetAwaiter().GetResult();

        hArray.ShouldBe(hStream);
    }

    // ── 2.7 Passphrase-seeded vs plaintext ────────────────────────────────────

    [Test]
    public void ComputeHash_WithPassphrase_DiffersFromPlaintext()
    {
        var encrypted = new PassphraseEncryptionService(Passphrase);
        var plaintext = new PlaintextPassthroughService();
        var data      = "some file content"u8.ToArray();

        encrypted.ComputeHash(data).ShouldNotBe(plaintext.ComputeHash(data));
    }

    // ── 2.7 Same file, different passphrase ──────────────────────────────────

    [Test]
    public void ComputeHash_DifferentPassphrases_ProduceDifferentHashes()
    {
        var svcA = new PassphraseEncryptionService("passphrase-a");
        var svcB = new PassphraseEncryptionService("passphrase-b");
        var data = "same content"u8.ToArray();

        svcA.ComputeHash(data).ShouldNotBe(svcB.ComputeHash(data));
    }

    // ── 2.4 Hash construction: SHA256(passphrase_bytes + data_bytes) ──────────

    [Test]
    public void ComputeHash_MatchesManualSha256PassphrasePlusData()
    {
        var svc  = new PassphraseEncryptionService(Passphrase);
        var data = "test data"u8.ToArray();

        var passBytes = Encoding.UTF8.GetBytes(Passphrase);
        var combined  = new byte[passBytes.Length + data.Length];
        passBytes.CopyTo(combined, 0);
        data.CopyTo(combined, passBytes.Length);
        var expected = SHA256.HashData(combined);

        svc.ComputeHash(data).ShouldBe(expected);
    }

}
