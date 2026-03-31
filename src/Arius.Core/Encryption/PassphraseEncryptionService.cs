using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Arius.Core.Encryption;

/// <summary>
/// Encryption service that writes AES-256-GCM (ArGCM1 format) and reads both
/// AES-256-GCM (ArGCM1) and legacy AES-256-CBC (Salted__) via magic-byte auto-detection.
///
/// Write format (ArGCM1):
///   Magic(6) | Salt(16) | Iterations(4 LE uint32) | Nonce₀(12)
///   For each 64 KiB block: Length(4 LE uint32) | Ciphertext+Tag(Length+16)
///   Sentinel: Length(4)=0 | Tag(16)
///
/// Legacy read format (AES-256-CBC, openssl-compatible):
///   "Salted__"(8) | Salt(8) | Ciphertext
///   Key derivation: PBKDF2-SHA256, 10,000 iterations.
///
/// Hash construction: SHA256(passphrase_bytes + data_bytes) — backwards-compatible.
/// </summary>
public sealed class PassphraseEncryptionService : IEncryptionService
{
    /// <inheritdoc/>
    public bool IsEncrypted => true;

    // ── CBC constants ────────────────────────────────────────────────────────────
    private const int CbcSaltSize   = 8;
    private const int CbcKeySize    = 32; // AES-256
    private const int CbcIvSize     = 16; // AES block size
    private const int CbcPbkdf2Iter = 10_000;
    private static readonly byte[] SaltedMagic = "Salted__"u8.ToArray();

    // ── GCM constants ────────────────────────────────────────────────────────────
    private const int GcmSaltSize       = 16;
    private const int GcmKeySize        = 32;  // AES-256
    private const int GcmNonceSize      = 12;
    private const int GcmTagSize        = 16;
    private const int GcmBlockSize      = 64 * 1024; // 64 KiB
    private const int GcmPbkdf2Iter     = 100_000;
    private const uint GcmMaxPbkdf2Iter = 10_000_000; // sanity cap: reject crafted blobs
    private static readonly byte[] GcmMagic = "ArGCM1"u8.ToArray(); // 6 bytes

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
        return new AesGcmEncryptingStream(inner, _passphraseBytes);
    }

    /// <summary>
    /// Test-only helper: wraps <paramref name="inner"/> with AES-256-CBC encryption (legacy format).
    /// Used to manufacture CBC blobs for backwards-compatibility integration tests.
    /// </summary>
    internal Stream WrapForCbcEncryption(Stream inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return new AesCbcEncryptingStream(inner, _passphraseBytes);
    }

    /// <inheritdoc/>
    public Stream WrapForDecryption(Stream inner)
    {
        ArgumentNullException.ThrowIfNull(inner);

        // Peek at magic bytes to detect scheme; requires a seekable or peekable stream.
        // We buffer the magic prefix into a PeekStream to avoid consuming it.
        var peek = new PeekStream(inner);
        var magic6 = peek.PeekBytes(GcmMagic.Length);
        var magic8 = peek.PeekBytes(SaltedMagic.Length);

        if (magic6.AsSpan().SequenceEqual(GcmMagic))
            return new AesGcmDecryptingStream(peek, _passphraseBytes);

        if (magic8.AsSpan().SequenceEqual(SaltedMagic))
            return new AesCbcDecryptingStream(peek, _passphraseBytes);

        throw new InvalidDataException(
            "Stream does not begin with a recognised Arius encryption magic (ArGCM1 or Salted__).");
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

    // ── Nonce derivation helper ──────────────────────────────────────────────────

    /// <summary>
    /// Computes nonce_i = nonce₀ XOR little_endian_bytes(i, 12).
    /// Prevents nonce reuse and binds each block to its position.
    /// </summary>
    private static byte[] DeriveNonce(byte[] nonce0, uint blockIndex)
    {
        var counter = new byte[GcmNonceSize];
        BinaryPrimitives.WriteUInt32LittleEndian(counter, blockIndex);
        // remaining 8 bytes stay zero — XOR with zero is identity

        var nonce = new byte[GcmNonceSize];
        for (var i = 0; i < GcmNonceSize; i++)
            nonce[i] = (byte)(nonce0[i] ^ counter[i]);
        return nonce;
    }

    // ── CBC key derivation ───────────────────────────────────────────────────────

    private static (byte[] key, byte[] iv) DeriveCbcKeyIv(byte[] passphraseBytes, byte[] salt)
    {
        var derived = Rfc2898DeriveBytes.Pbkdf2(
            passphraseBytes,
            salt,
            CbcPbkdf2Iter,
            HashAlgorithmName.SHA256,
            CbcKeySize + CbcIvSize);

        return (derived[..CbcKeySize], derived[CbcKeySize..]);
    }

    // ── GCM key derivation ───────────────────────────────────────────────────────

    private static byte[] DeriveGcmKey(byte[] passphraseBytes, byte[] salt, int iterations)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            passphraseBytes,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            GcmKeySize);
    }

    // ── AES-GCM encrypting stream ────────────────────────────────────────────────

    /// <summary>
    /// Write-only stream that encrypts data as AES-256-GCM using the ArGCM1 chunked format.
    /// Data is buffered in 64 KiB blocks; each block is encrypted independently with a
    /// counter-derived nonce. A sentinel block is written on dispose to prevent truncation attacks.
    /// </summary>
    private sealed class AesGcmEncryptingStream : Stream
    {
        private readonly Stream _inner;
        private readonly AesGcm  _aesGcm;
        private readonly byte[]  _nonce0;
        private readonly byte[]  _plainBuffer;
        private readonly byte[]  _cipherBuffer;
        private readonly byte[]  _tagBuffer;
        private int   _plainOffset;
        private uint  _blockIndex;
        private bool  _disposed;

        public AesGcmEncryptingStream(Stream inner, byte[] passphraseBytes)
        {
            _inner = inner;

            var salt       = RandomNumberGenerator.GetBytes(GcmSaltSize);
            _nonce0        = RandomNumberGenerator.GetBytes(GcmNonceSize);
            var key        = DeriveGcmKey(passphraseBytes, salt, GcmPbkdf2Iter);
            _aesGcm        = new AesGcm(key, GcmTagSize);
            _plainBuffer   = new byte[GcmBlockSize];
            _cipherBuffer  = new byte[GcmBlockSize];
            _tagBuffer     = new byte[GcmTagSize];

            // Write 38-byte header: magic(6) + salt(16) + iterations(4 LE) + nonce₀(12)
            inner.Write(GcmMagic);
            inner.Write(salt);
            var iterBytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(iterBytes, GcmPbkdf2Iter);
            inner.Write(iterBytes);
            inner.Write(_nonce0);
        }

        public override bool CanWrite => true;
        public override bool CanRead  => false;
        public override bool CanSeek  => false;

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            while (!buffer.IsEmpty)
            {
                var space     = GcmBlockSize - _plainOffset;
                var toCopy    = Math.Min(space, buffer.Length);
                buffer[..toCopy].CopyTo(_plainBuffer.AsSpan(_plainOffset));
                _plainOffset += toCopy;
                buffer        = buffer[toCopy..];

                if (_plainOffset == GcmBlockSize)
                    FlushBlock();
            }
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            await WriteAsync(buffer.AsMemory(offset, count), ct);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            while (!buffer.IsEmpty)
            {
                var space  = GcmBlockSize - _plainOffset;
                var toCopy = Math.Min(space, buffer.Length);
                buffer[..toCopy].CopyTo(_plainBuffer.AsMemory(_plainOffset));
                _plainOffset += toCopy;
                buffer        = buffer[toCopy..];

                if (_plainOffset == GcmBlockSize)
                    await FlushBlockAsync(ct);
            }
        }

        private void FlushBlock()
        {
            if (_plainOffset == 0) return;

            var nonce      = DeriveNonce(_nonce0, _blockIndex++);
            var plain      = _plainBuffer.AsSpan(0, _plainOffset);
            var cipher     = _cipherBuffer.AsSpan(0, _plainOffset);
            var tag        = _tagBuffer.AsSpan();

            _aesGcm.Encrypt(nonce, plain, cipher, tag);

            // Write: length(4 LE) + ciphertext + tag
            var lenBytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(lenBytes, (uint)_plainOffset);
            _inner.Write(lenBytes);
            _inner.Write(cipher);
            _inner.Write(tag);

            _plainOffset = 0;
        }

        private async Task FlushBlockAsync(CancellationToken ct)
        {
            if (_plainOffset == 0) return;

            var nonce  = DeriveNonce(_nonce0, _blockIndex++);
            var plain  = _plainBuffer.AsMemory(0, _plainOffset);
            var cipher = _cipherBuffer.AsMemory(0, _plainOffset);
            var tag    = _tagBuffer.AsMemory();

            _aesGcm.Encrypt(nonce, plain.Span, cipher.Span, tag.Span);

            var lenBytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(lenBytes, (uint)_plainOffset);
            await _inner.WriteAsync(lenBytes, ct);
            await _inner.WriteAsync(cipher, ct);
            await _inner.WriteAsync(tag, ct);

            _plainOffset = 0;
        }

        private void WriteSentinel()
        {
            // Flush any remaining buffered data first
            if (_plainOffset > 0) FlushBlock();

            // Sentinel: length=0 + tag (authenticates end-of-stream under next nonce)
            var nonce    = DeriveNonce(_nonce0, _blockIndex++);
            var tag      = new byte[GcmTagSize];
            _aesGcm.Encrypt(nonce, ReadOnlySpan<byte>.Empty, Span<byte>.Empty, tag);

            var lenBytes = new byte[4]; // all zeros = 0
            _inner.Write(lenBytes);
            _inner.Write(tag);
        }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);

        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                WriteSentinel();
                _aesGcm.Dispose();
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

    // ── AES-GCM decrypting stream ────────────────────────────────────────────────

    /// <summary>
    /// Read-only stream that decrypts AES-256-GCM data in the ArGCM1 chunked format.
    /// Reads the 38-byte header, derives the key via PBKDF2, then decrypts blocks on demand.
    /// Signals EOF on the sentinel block. Throws <see cref="AuthenticationTagMismatchException"/>
    /// on tampered ciphertext and <see cref="InvalidDataException"/> on truncation.
    /// </summary>
    private sealed class AesGcmDecryptingStream : Stream
    {
        private readonly Stream _inner;
        private readonly AesGcm  _aesGcm;
        private readonly byte[]  _nonce0;
        private readonly byte[]  _plainBuffer;
        private readonly byte[]  _cipherBuffer;
        private readonly byte[]  _tagBuffer;
        private int  _plainLength;
        private int  _plainOffset;
        private uint _blockIndex;
        private bool _eof;

        public AesGcmDecryptingStream(Stream inner, byte[] passphraseBytes)
        {
            _inner = inner;

            // Read 38-byte header: magic(6) + salt(16) + iterations(4 LE) + nonce₀(12)
            var header = new byte[6 + GcmSaltSize + 4 + GcmNonceSize]; // = 38
            inner.ReadExactly(header);

            if (!header.AsSpan(0, GcmMagic.Length).SequenceEqual(GcmMagic))
                throw new InvalidDataException("Stream does not begin with ArGCM1 magic.");

            var salt           = header.AsSpan(6, GcmSaltSize).ToArray();
            var iterRaw        = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(6 + GcmSaltSize, 4));
            if (iterRaw == 0 || iterRaw > GcmMaxPbkdf2Iter)
                throw new InvalidDataException(
                    $"ArGCM1 header contains an out-of-range PBKDF2 iteration count ({iterRaw}). " +
                    $"Expected 1–{GcmMaxPbkdf2Iter}.");
            var iterations     = (int)iterRaw;
            _nonce0            = header.AsSpan(6 + GcmSaltSize + 4, GcmNonceSize).ToArray();

            var key     = DeriveGcmKey(passphraseBytes, salt, iterations);
            _aesGcm     = new AesGcm(key, GcmTagSize);
            _plainBuffer  = new byte[GcmBlockSize];
            _cipherBuffer = new byte[GcmBlockSize + GcmTagSize];
            _tagBuffer    = new byte[GcmTagSize];
        }

        public override bool CanRead  => true;
        public override bool CanWrite => false;
        public override bool CanSeek  => false;

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var totalRead = 0;

            while (!buffer.IsEmpty && !_eof)
            {
                if (_plainOffset < _plainLength)
                {
                    // Serve from decrypted buffer
                    var available = _plainLength - _plainOffset;
                    var toCopy    = Math.Min(available, buffer.Length);
                    _plainBuffer.AsSpan(_plainOffset, toCopy).CopyTo(buffer);
                    _plainOffset += toCopy;
                    buffer        = buffer[toCopy..];
                    totalRead    += toCopy;
                }
                else
                {
                    // Decode next block
                    if (!ReadAndDecryptNextBlock())
                        break;
                }
            }

            return totalRead;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            await ReadAsync(buffer.AsMemory(offset, count), ct);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var totalRead = 0;

            while (!buffer.IsEmpty && !_eof)
            {
                if (_plainOffset < _plainLength)
                {
                    var available = _plainLength - _plainOffset;
                    var toCopy    = Math.Min(available, buffer.Length);
                    _plainBuffer.AsMemory(_plainOffset, toCopy).CopyTo(buffer);
                    _plainOffset += toCopy;
                    buffer        = buffer[toCopy..];
                    totalRead    += toCopy;
                }
                else
                {
                    if (!await ReadAndDecryptNextBlockAsync(ct))
                        break;
                }
            }

            return totalRead;
        }

        private bool ReadAndDecryptNextBlock()
        {
            // Read 4-byte block length
            var lenBytes = new byte[4];
            _inner.ReadExactly(lenBytes);
            var plainLen = BinaryPrimitives.ReadUInt32LittleEndian(lenBytes);

            if (plainLen == 0)
            {
                // Sentinel — read and verify tag
                _inner.ReadExactly(_tagBuffer);
                var nonce = DeriveNonce(_nonce0, _blockIndex++);
                // Throws AuthenticationTagMismatchException on tamper
                _aesGcm.Decrypt(nonce, ReadOnlySpan<byte>.Empty, _tagBuffer, Span<byte>.Empty);
                _eof = true;
                return false;
            }

            if (plainLen > GcmBlockSize)
                throw new InvalidDataException($"Block length {plainLen} exceeds maximum {GcmBlockSize}.");

            var cipherLen   = (int)plainLen;
            var cipherSpan  = _cipherBuffer.AsSpan(0, cipherLen);
            _inner.ReadExactly(cipherSpan);
            _inner.ReadExactly(_tagBuffer);

            var nonce2    = DeriveNonce(_nonce0, _blockIndex++);
            var plainSpan = _plainBuffer.AsSpan(0, cipherLen);
            _aesGcm.Decrypt(nonce2, cipherSpan, _tagBuffer, plainSpan);

            _plainLength = cipherLen;
            _plainOffset = 0;
            return true;
        }

        private async Task<bool> ReadAndDecryptNextBlockAsync(CancellationToken ct)
        {
            var lenBytes = new byte[4];
            await _inner.ReadExactlyAsync(lenBytes, ct);
            var plainLen = BinaryPrimitives.ReadUInt32LittleEndian(lenBytes);

            if (plainLen == 0)
            {
                await _inner.ReadExactlyAsync(_tagBuffer, ct);
                var nonce = DeriveNonce(_nonce0, _blockIndex++);
                _aesGcm.Decrypt(nonce, ReadOnlySpan<byte>.Empty, _tagBuffer, Span<byte>.Empty);
                _eof = true;
                return false;
            }

            if (plainLen > GcmBlockSize)
                throw new InvalidDataException($"Block length {plainLen} exceeds maximum {GcmBlockSize}.");

            var cipherLen  = (int)plainLen;
            await _inner.ReadExactlyAsync(_cipherBuffer.AsMemory(0, cipherLen), ct);
            await _inner.ReadExactlyAsync(_tagBuffer, ct);

            var nonce2    = DeriveNonce(_nonce0, _blockIndex++);
            var plainSpan = _plainBuffer.AsSpan(0, cipherLen);
            _aesGcm.Decrypt(nonce2, _cipherBuffer.AsSpan(0, cipherLen), _tagBuffer, plainSpan);

            _plainLength = cipherLen;
            _plainOffset = 0;
            return true;
        }

        public override void Flush() { }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _aesGcm.Dispose();
            base.Dispose(disposing);
        }

        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin)        => throw new NotSupportedException();
        public override void SetLength(long value)                        => throw new NotSupportedException();
    }

    // ── CBC encrypting stream ────────────────────────────────────────────────────

    /// <summary>
    /// Write-only stream that writes the openssl header (Salted__ + salt) then AES-CBC ciphertext.
    /// </summary>
    private sealed class AesCbcEncryptingStream : Stream
    {
        private readonly CryptoStream _cryptoStream;
        private bool _disposed;

        public AesCbcEncryptingStream(Stream inner, byte[] passphraseBytes)
        {
            var salt = RandomNumberGenerator.GetBytes(CbcSaltSize);
            var (key, iv) = DeriveCbcKeyIv(passphraseBytes, salt);

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

    // ── CBC decrypting stream ────────────────────────────────────────────────────

    /// <summary>
    /// Read-only stream that reads and strips the openssl header, then decrypts AES-CBC.
    /// </summary>
    private sealed class AesCbcDecryptingStream : Stream
    {
        private readonly CryptoStream _cryptoStream;

        public AesCbcDecryptingStream(Stream inner, byte[] passphraseBytes)
        {
            // Read and validate Salted__ magic (8 bytes) + salt (8 bytes)
            var header = new byte[SaltedMagic.Length + CbcSaltSize];
            inner.ReadExactly(header);

            if (!header.AsSpan(0, SaltedMagic.Length).SequenceEqual(SaltedMagic))
                throw new InvalidDataException("Stream does not begin with OpenSSL 'Salted__' header.");

            var salt = header[SaltedMagic.Length..];
            var (key, iv) = DeriveCbcKeyIv(passphraseBytes, salt);

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

    // ── PeekStream helper ────────────────────────────────────────────────────────

    /// <summary>
    /// Wraps a stream and buffers the first N bytes so they can be peeked
    /// (read without advancing the logical position) before actual reading begins.
    /// </summary>
    private sealed class PeekStream(Stream inner) : Stream
    {
        private readonly byte[] _peekBuf = new byte[8]; // max peek = 8 (length of "Salted__")
        private int _peekBufLen;
        private int _peekBufOffset;

        /// <summary>
        /// Returns up to <paramref name="n"/> bytes from the front of the stream
        /// without advancing the read position.
        /// </summary>
        public byte[] PeekBytes(int n)
        {
            if (n > _peekBuf.Length)
                throw new ArgumentOutOfRangeException(nameof(n));

            // Loop until we have n bytes or the underlying stream signals EOF
            while (_peekBufLen < n)
            {
                var needed = n - _peekBufLen;
                var read   = inner.Read(_peekBuf, _peekBufLen, needed);
                if (read == 0)
                    break; // EOF — return however many bytes we managed to read
                _peekBufLen += read;
            }

            return _peekBuf[.._peekBufLen];
        }

        public override bool CanRead  => true;
        public override bool CanWrite => false;
        public override bool CanSeek  => false;

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var totalRead = 0;

            // Drain peeked bytes first
            if (_peekBufOffset < _peekBufLen)
            {
                var available = _peekBufLen - _peekBufOffset;
                var toCopy    = Math.Min(available, buffer.Length);
                _peekBuf.AsSpan(_peekBufOffset, toCopy).CopyTo(buffer);
                _peekBufOffset += toCopy;
                buffer          = buffer[toCopy..];
                totalRead      += toCopy;
            }

            if (!buffer.IsEmpty)
                totalRead += inner.Read(buffer);

            return totalRead;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            await ReadAsync(buffer.AsMemory(offset, count), ct);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var totalRead = 0;

            if (_peekBufOffset < _peekBufLen)
            {
                var available = _peekBufLen - _peekBufOffset;
                var toCopy    = Math.Min(available, buffer.Length);
                _peekBuf.AsMemory(_peekBufOffset, toCopy).CopyTo(buffer);
                _peekBufOffset += toCopy;
                buffer          = buffer[toCopy..];
                totalRead      += toCopy;
            }

            if (!buffer.IsEmpty)
                totalRead += await inner.ReadAsync(buffer, ct);

            return totalRead;
        }

        public override void Flush() { }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override long Length   => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin)        => throw new NotSupportedException();
        public override void SetLength(long value)                        => throw new NotSupportedException();
    }
}
