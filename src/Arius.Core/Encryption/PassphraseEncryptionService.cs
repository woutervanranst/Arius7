using System.Security.Cryptography;
using System.Text;

namespace Arius.Core.Encryption;

/// <summary>
/// AES-256-CBC encryption service, openssl-compatible format:
///   Salted__ (8 bytes) | salt (8 bytes) | ciphertext
/// Key derivation: PBKDF2-SHA256, 10,000 iterations, key=32 bytes, iv=16 bytes.
/// Decryptable with: openssl enc -d -aes-256-cbc -pbkdf2 -iter 10000 -pass pass:&lt;passphrase&gt;
///
/// Hash construction: SHA256(passphrase_bytes + data_bytes) — backwards-compatible with previous Arius.
/// </summary>
public sealed class PassphraseEncryptionService : IEncryptionService
{
    /// <inheritdoc/>
    public bool IsEncrypted => true;

    private const int SaltSize       = 8;
    private const int KeySize        = 32; // AES-256
    private const int IvSize         = 16; // AES block size
    private const int Pbkdf2Iter     = 10_000;
    private static readonly byte[] SaltedMagic = "Salted__"u8.ToArray();

    private readonly byte[] _passphraseBytes;

    public PassphraseEncryptionService(string passphrase)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        _passphraseBytes = Encoding.UTF8.GetBytes(passphrase);
    }

    /// <inheritdoc/>
    public Stream WrapForEncryption(Stream inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return new EncryptingStream(inner, _passphraseBytes);
    }

    /// <inheritdoc/>
    public Stream WrapForDecryption(Stream inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return new DecryptingStream(inner, _passphraseBytes);
    }

    /// <inheritdoc/>
    public byte[] ComputeHash(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        // SHA256(passphrase_bytes + data_bytes) — literal concat, not HMAC
        var input = new byte[_passphraseBytes.Length + data.Length];
        _passphraseBytes.CopyTo(input, 0);
        data.CopyTo(input, _passphraseBytes.Length);
        return SHA256.HashData(input);
    }

    /// <inheritdoc/>
    public async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(_passphraseBytes);

        var buffer = new byte[81920];
        int read;
        while ((read = await data.ReadAsync(buffer, cancellationToken)) > 0)
            sha.AppendData(buffer, 0, read);

        return sha.GetHashAndReset();
    }

    // ── Key derivation ─────────────────────────────────────────────────────────

    private static (byte[] key, byte[] iv) DeriveKeyIv(byte[] passphraseBytes, byte[] salt)
    {
        // openssl EVP_BytesToKey equivalent via PBKDF2-SHA256
        using var pbkdf2 = new Rfc2898DeriveBytes(
            passphraseBytes,
            salt,
            Pbkdf2Iter,
            HashAlgorithmName.SHA256);

        var key = pbkdf2.GetBytes(KeySize);
        var iv  = pbkdf2.GetBytes(IvSize);
        return (key, iv);
    }

    // ── Encrypting stream ──────────────────────────────────────────────────────

    /// <summary>
    /// Write-only stream that writes the openssl header (Salted__ + salt) then AES-CBC ciphertext.
    /// </summary>
    private sealed class EncryptingStream : Stream
    {
        private readonly CryptoStream _cryptoStream;
        private bool _disposed;

        public EncryptingStream(Stream inner, byte[] passphraseBytes)
        {
            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            var (key, iv) = DeriveKeyIv(passphraseBytes, salt);

            // Write magic + salt header before any ciphertext
            inner.Write(SaltedMagic);
            inner.Write(salt);

            using var aes = Aes.Create();
            aes.KeySize = 256;
            aes.Mode    = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = key;
            aes.IV  = iv;

            _cryptoStream = new CryptoStream(inner, aes.CreateEncryptor(), CryptoStreamMode.Write, leaveOpen: true);
        }

        public override bool CanWrite => true;
        public override bool CanRead  => false;
        public override bool CanSeek  => false;

        public override void Write(byte[] buffer, int offset, int count) =>
            _cryptoStream.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) =>
            _cryptoStream.Write(buffer);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            _cryptoStream.WriteAsync(buffer, offset, count, ct);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
            _cryptoStream.WriteAsync(buffer, ct);

        public override void Flush() => _cryptoStream.Flush();

        public override Task FlushAsync(CancellationToken ct) => _cryptoStream.FlushAsync(ct);

        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _cryptoStream.FlushFinalBlock();
                _cryptoStream.Dispose();
                _disposed = true;
            }
            base.Dispose(disposing);
        }

        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int  Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin)       => throw new NotSupportedException();
        public override void SetLength(long value)                       => throw new NotSupportedException();
    }

    // ── Decrypting stream ──────────────────────────────────────────────────────

    /// <summary>
    /// Read-only stream that reads and strips the openssl header, then decrypts AES-CBC.
    /// </summary>
    private sealed class DecryptingStream : Stream
    {
        private readonly CryptoStream _cryptoStream;

        public DecryptingStream(Stream inner, byte[] passphraseBytes)
        {
            // Read and validate Salted__ magic (8 bytes) + salt (8 bytes)
            var header = new byte[SaltedMagic.Length + SaltSize];
            inner.ReadExactly(header);

            if (!header.AsSpan(0, SaltedMagic.Length).SequenceEqual(SaltedMagic))
                throw new InvalidDataException("Stream does not begin with OpenSSL 'Salted__' header.");

            var salt = header[SaltedMagic.Length..];
            var (key, iv) = DeriveKeyIv(passphraseBytes, salt);

            using var aes = Aes.Create();
            aes.KeySize = 256;
            aes.Mode    = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = key;
            aes.IV  = iv;

            _cryptoStream = new CryptoStream(inner, aes.CreateDecryptor(), CryptoStreamMode.Read, leaveOpen: true);
        }

        public override bool CanRead  => true;
        public override bool CanWrite => false;
        public override bool CanSeek  => false;

        public override int Read(byte[] buffer, int offset, int count) =>
            _cryptoStream.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) =>
            _cryptoStream.Read(buffer);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            _cryptoStream.ReadAsync(buffer, offset, count, ct);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            _cryptoStream.ReadAsync(buffer, ct);

        public override void Flush() { }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _cryptoStream.Dispose();
            base.Dispose(disposing);
        }

        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin)        => throw new NotSupportedException();
        public override void SetLength(long value)                        => throw new NotSupportedException();
    }
}
