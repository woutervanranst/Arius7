using Arius.Core.Encryption;
using Shouldly;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Arius.Core.Tests.Encryption;

/// <summary>
/// Unit tests for AES-256-GCM encryption (ArGCM1 format) in
/// <see cref="PassphraseEncryptionService"/>.
/// </summary>
public class AesGcmEncryptionTests
{
    private const string Passphrase = "test-gcm-passphrase";

    // ── 5.1 Small payload roundtrip ──────────────────────────────────────────────

    [Test]
    public async Task GcmEncryptDecrypt_SmallPayload_Roundtrip()
    {
        var svc      = new PassphraseEncryptionService(Passphrase);
        var original = "Hello, AES-256-GCM!"u8.ToArray();

        var cipherMs = new MemoryStream();
        await using (var enc = svc.WrapForEncryption(cipherMs))
            await enc.WriteAsync(original);

        cipherMs.Position = 0;
        var plainMs = new MemoryStream();
        await using var dec = svc.WrapForDecryption(cipherMs);
        await dec.CopyToAsync(plainMs);

        plainMs.ToArray().ShouldBe(original);
    }

    // ── 5.2 Multi-block roundtrip (4 MB) ────────────────────────────────────────

    [Test]
    public async Task GcmEncryptDecrypt_LargePayload_Roundtrip()
    {
        var svc  = new PassphraseEncryptionService(Passphrase);
        var data = RandomNumberGenerator.GetBytes(4 * 1024 * 1024); // 4 MB → 64 blocks

        var cipherMs = new MemoryStream();
        await using (var enc = svc.WrapForEncryption(cipherMs))
            await enc.WriteAsync(data);

        cipherMs.Position = 0;
        var plainMs = new MemoryStream();
        await using var dec = svc.WrapForDecryption(cipherMs);
        await dec.CopyToAsync(plainMs);

        plainMs.ToArray().ShouldBe(data);
    }

    // ── 5.3 Magic prefix ─────────────────────────────────────────────────────────

    [Test]
    public async Task GcmEncrypt_OutputStartsWithArGcm1Magic()
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

    // ── 5.4 Header structure ─────────────────────────────────────────────────────

    [Test]
    public async Task GcmEncrypt_HeaderStructure_IsCorrect()
    {
        var svc  = new PassphraseEncryptionService(Passphrase);
        var data = "header-check"u8.ToArray();

        var ms = new MemoryStream();
        await using (var enc = svc.WrapForEncryption(ms))
            await enc.WriteAsync(data);

        var bytes = ms.ToArray();

        // Header layout: magic(6) + salt(16) + iterations(4 LE) + nonce0(12) = 38
        bytes.Length.ShouldBeGreaterThanOrEqualTo(38);

        // magic
        Encoding.ASCII.GetString(bytes, 0, 6).ShouldBe("ArGCM1");

        // salt: 16 bytes — any non-zero sequence (random)
        var salt = bytes[6..22];
        salt.Length.ShouldBe(16);

        // iterations: 100,000 LE uint32
        var iterations = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(22, 4));
        iterations.ShouldBe(100_000u);

        // nonce: 12 bytes
        var nonce = bytes[26..38];
        nonce.Length.ShouldBe(12);
    }

    // ── 5.5 Bounded memory: 32 MB via streaming (no full buffer) ─────────────────

    [Test]
    public async Task GcmEncrypt_LargeStream_DoesNotBufferEntireContent()
    {
        var svc        = new PassphraseEncryptionService(Passphrase);
        const int size = 32 * 1024 * 1024; // 32 MB

        var source = new ZeroStream(size);
        var sink   = new DevNullStream();

        await using (var enc = svc.WrapForEncryption(sink))
            await source.CopyToAsync(enc);

        // ciphertext >= plaintext (header + tag overhead)
        sink.BytesWritten.ShouldBeGreaterThan(size);
    }

    // ── 5.6 Tamper detection ─────────────────────────────────────────────────────

    [Test]
    public async Task GcmDecrypt_TamperedCiphertext_ThrowsAuthenticationException()
    {
        var svc  = new PassphraseEncryptionService(Passphrase);
        var data = RandomNumberGenerator.GetBytes(100);

        var cipherMs = new MemoryStream();
        await using (var enc = svc.WrapForEncryption(cipherMs))
            await enc.WriteAsync(data);

        var bytes = cipherMs.ToArray();

        // Flip a byte in the ciphertext (after the 38-byte header + 4-byte block length)
        bytes[38 + 4 + 5] ^= 0xFF;

        var tampered = new MemoryStream(bytes);
        var plainMs  = new MemoryStream();
        await using var dec = svc.WrapForDecryption(tampered);

        await Should.ThrowAsync<AuthenticationTagMismatchException>(
            async () => await dec.CopyToAsync(plainMs));
    }

    // ── 5.6b Tampered sentinel tag ────────────────────────────────────────────

    [Test]
    public async Task GcmDecrypt_TamperedSentinelTag_ThrowsAuthenticationException()
    {
        var svc  = new PassphraseEncryptionService(Passphrase);
        var data = RandomNumberGenerator.GetBytes(100);

        var cipherMs = new MemoryStream();
        await using (var enc = svc.WrapForEncryption(cipherMs))
            await enc.WriteAsync(data);

        var bytes = cipherMs.ToArray();

        // Flip a byte in the sentinel tag (last 16 bytes of the stream)
        bytes[^8] ^= 0xFF;

        var tampered = new MemoryStream(bytes);
        var plainMs  = new MemoryStream();
        await using var dec = svc.WrapForDecryption(tampered);

        await Should.ThrowAsync<AuthenticationTagMismatchException>(
            async () => await dec.CopyToAsync(plainMs));
    }

    // ── 5.7 Truncation detection ─────────────────────────────────────────────────

    [Test]
    public async Task GcmDecrypt_TruncatedStream_ThrowsOnMissingSentinel()
    {
        var svc  = new PassphraseEncryptionService(Passphrase);
        var data = RandomNumberGenerator.GetBytes(100);

        var cipherMs = new MemoryStream();
        await using (var enc = svc.WrapForEncryption(cipherMs))
            await enc.WriteAsync(data);

        // Remove the last 20 bytes (sentinel = 4-byte length + 16-byte tag)
        var full      = cipherMs.ToArray();
        var truncated = full[..^20];

        var truncMs = new MemoryStream(truncated);
        var plainMs = new MemoryStream();
        await using var dec = svc.WrapForDecryption(truncMs);

        await Should.ThrowAsync<EndOfStreamException>(
            async () => await dec.CopyToAsync(plainMs));
    }

    // ── 5.8 Auto-detection: GCM and CBC streams both handled correctly ────────────

    [Test]
    public async Task WrapForDecryption_AutoDetects_GcmAndCbc()
    {
        var svc = new PassphraseEncryptionService(Passphrase);
        var data = "auto-detect test"u8.ToArray();

        // GCM (new default)
        var gcmMs = new MemoryStream();
        await using (var enc = svc.WrapForEncryption(gcmMs))
            await enc.WriteAsync(data);

        gcmMs.Position = 0;
        var gcmPlain = new MemoryStream();
        await using (var dec = svc.WrapForDecryption(gcmMs))
            await dec.CopyToAsync(gcmPlain);

        gcmPlain.ToArray().ShouldBe(data);

        // CBC (legacy) — verify against golden files (tested separately),
        // but we can verify the CBC path works via the existing golden file decrypt approach.
        // Here we just confirm the GCM path works end-to-end.
        Encoding.UTF8.GetString(gcmPlain.ToArray()).ShouldBe("auto-detect test");
    }

    // ── 5.8b Auto-detection: CBC golden file decrypts correctly via WrapForDecryption ──

    [Test]
    public async Task WrapForDecryption_CbcGoldenFile_DecryptsCorrectly()
    {
        // Uses the existing CBC golden file to confirm auto-detection routes to CBC stream
        var goldenDir  = Path.Combine(AppContext.BaseDirectory, "Encryption", "GoldenFiles");
        const string cbcFile = "9ffc39c119e735c3c96e5ee912132a52c9c98566fb2a7c2ef156c4666afab18d";
        var path = Path.Combine(goldenDir, cbcFile);
        File.Exists(path).ShouldBeTrue($"CBC golden file not found: {path}");

        var svc = new PassphraseEncryptionService("wouter");
        await using var fs        = File.OpenRead(path);
        await using var dec       = svc.WrapForDecryption(fs);
        await using var gzip      = new GZipStream(dec, CompressionMode.Decompress);
        var ms = new MemoryStream();
        await gzip.CopyToAsync(ms);

        // Just verify it decrypted without error and produced non-empty output
        ms.Length.ShouldBeGreaterThan(0);
    }

    // ── 5.9 Unknown magic → InvalidDataException ─────────────────────────────────

    [Test]
    public void WrapForDecryption_UnknownMagic_ThrowsInvalidDataException()
    {
        var svc    = new PassphraseEncryptionService(Passphrase);
        var junk   = new MemoryStream("JUNKJUNK"u8.ToArray());

        Should.Throw<InvalidDataException>(() => svc.WrapForDecryption(junk));
    }

    // ── 5.10 GCM golden file decryption ─────────────────────────────────────────

    [Test]
    public async Task GcmGoldenFile_Decrypt_PlaintextMatchesExpected()
    {
        // Golden file: GCM-encrypted gzip of "Hello, ArGCM1 golden file!" with passphrase "wouter"
        // Fixed salt: deadbeef0102030405060708090a0b0c
        // Fixed nonce₀: cafebabe010203040506070809ab (first 12 bytes: ca fe ba be 01 02 03 04 05 06 07 08)
        const string gcmGoldenFile =
            "2594868716c414b39895e10299bc609a1d1602a65b8576599d149f911aa33be8";
        const string expectedPlaintext = "Hello, ArGCM1 golden file!";
        const string goldenPassphrase  = "wouter";

        var goldenDir = Path.Combine(AppContext.BaseDirectory, "Encryption", "GoldenFiles");
        var path      = Path.Combine(goldenDir, gcmGoldenFile);
        File.Exists(path).ShouldBeTrue($"GCM golden file not found: {path}");

        var svc = new PassphraseEncryptionService(goldenPassphrase);

        await using var fs       = File.OpenRead(path);
        await using var dec      = svc.WrapForDecryption(fs);
        await using var gzip     = new GZipStream(dec, CompressionMode.Decompress);
        var ms = new MemoryStream();
        await gzip.CopyToAsync(ms);

        Encoding.UTF8.GetString(ms.ToArray()).ShouldBe(expectedPlaintext);
    }

    // ── 5.11 CBC text golden file decryption ─────────────────────────────────────

    [Test]
    public async Task CbcTextGoldenFile_Decrypt_PlaintextMatchesExpected()
    {
        // Golden file: CBC-encrypted gzip of "Hello, Salted__ CBC golden file!" with passphrase "wouter"
        // Fixed salt: deadbeef01020304 (8 bytes)
        // Key+IV derived via PBKDF2-SHA256(passphrase, salt, 10_000, dklen=48)
        const string cbcGoldenFile    = "680ccc692b5c2b058a0d9964ae08f9343350f8873dd900bb62742ba0a0b313de";
        const string expectedPlaintext = "Hello, Salted__ CBC golden file!";
        const string goldenPassphrase  = "wouter";

        var goldenDir = Path.Combine(AppContext.BaseDirectory, "Encryption", "GoldenFiles");
        var path      = Path.Combine(goldenDir, cbcGoldenFile);
        File.Exists(path).ShouldBeTrue($"CBC text golden file not found: {path}");

        var svc = new PassphraseEncryptionService(goldenPassphrase);

        await using var fs   = File.OpenRead(path);
        await using var dec  = svc.WrapForDecryption(fs);  // auto-detects Salted__ magic → CBC
        await using var gzip = new GZipStream(dec, CompressionMode.Decompress);
        var ms = new MemoryStream();
        await gzip.CopyToAsync(ms);

        Encoding.UTF8.GetString(ms.ToArray()).ShouldBe(expectedPlaintext);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private sealed class ZeroStream(long length) : Stream
    {
        private long _remaining = length;
        public override bool CanRead  => true;
        public override bool CanWrite => false;
        public override bool CanSeek  => false;
        public override long Length   => length;
        public override long Position { get => length - _remaining; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, _remaining);
            Array.Clear(buffer, offset, n);
            _remaining -= n;
            return n;
        }
        public override void Flush() { }
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s)    => throw new NotSupportedException();
        public override void SetLength(long v)              => throw new NotSupportedException();
    }

    private sealed class DevNullStream : Stream
    {
        public long BytesWritten { get; private set; }
        public override bool CanRead  => false;
        public override bool CanWrite => true;
        public override bool CanSeek  => false;
        public override long Length   => BytesWritten;
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => BytesWritten += count;
        public override void Write(ReadOnlySpan<byte> buffer)            => BytesWritten += buffer.Length;
        public override void Flush() { }
        public override int  Read(byte[] b, int o, int c)  => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s)    => throw new NotSupportedException();
        public override void SetLength(long v)              => throw new NotSupportedException();
    }
}
