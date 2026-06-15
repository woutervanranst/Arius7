using ZstdSharp;
using ZstdSharp.Unsafe;

namespace Arius.Core.Shared.Compression;

/// <summary>
/// zstd-based <see cref="ICompressionService"/> backed by ZstdSharp.Port (a pure-managed port of
/// facebook's reference libzstd). Writes standard RFC 8878 frames with the content checksum enabled;
/// reads auto-detect zstd vs legacy gzip from the stream header.
/// </summary>
[SharedWithinAssembly]
internal sealed class ZstdCompressionService : ICompressionService
{
    // Magic number at the start of a zstd frame (little-endian on disk).
    private static readonly byte[] ZstdMagic = [0x28, 0xB5, 0x2F, 0xFD]; // 0xFD2FB528

    // Legacy "+gzip" blobs are decoded by the gzip codec; reads delegate to it when a gzip frame is detected.
    private static readonly GZipCompressionService LegacyGzip = new();

    /// <summary>
    /// zstd compression level for new blobs. This is the one performance/size knob: higher = smaller
    /// but slower. 19 favours ratio (matching the previous gzip <c>SmallestSize</c> intent); benchmark
    /// against representative data before changing.
    /// </summary>
    public const int DefaultCompressionLevel = 19;

    private readonly int _level;

    public ZstdCompressionService(int compressionLevel = DefaultCompressionLevel) => _level = compressionLevel;

    /// <summary>
    /// zstd is verified inline on every chunk upload: it is the newer encoder, so we prove each frame
    /// round-trips at archive time (while the source is still on disk) rather than discovering an encoder
    /// bug on a future restore.
    /// </summary>
    public bool RequireRoundTripVerification => true;

    public Stream WrapForCompression(Stream destination, bool leaveOpen = true)
    {
        var stream = new CompressionStream(destination, _level, leaveOpen: leaveOpen);

        // XXH64 frame checksum (over the original data). Off by default in zstd; we enable it so any
        // decode-time corruption is loud — keeping parity with gzip's always-on CRC32.
        stream.SetParameter(ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
        return stream;
    }

    public Stream WrapForDecompression(Stream source, bool leaveOpen = false)
        => new AutoDetectDecompressionStream(source, leaveOpen);

    /// <summary>
    /// Read-only stream that peeks the leading magic bytes of <paramref name="source"/> on first read,
    /// selects the matching decompressor (zstd or legacy gzip), and delegates thereafter. Detection is
    /// lazy so neither sync nor async callers pay an eager blocking read.
    /// </summary>
    private sealed class AutoDetectDecompressionStream : Stream
    {
        private readonly Stream _source;
        private readonly bool _leaveOpen;
        private Stream? _inner;

        public AutoDetectDecompressionStream(Stream source, bool leaveOpen)
        {
            _source = source;
            _leaveOpen = leaveOpen;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            EnsureInitialized();
            return _inner!.Read(buffer);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await EnsureInitializedAsync(cancellationToken);
            return await _inner!.ReadAsync(buffer, cancellationToken);
        }

        private void EnsureInitialized()
        {
            if (_inner is not null)
                return;

            var header = new byte[4];
            var read = 0;
            while (read < header.Length)
            {
                var n = _source.Read(header.AsSpan(read));
                if (n == 0)
                    break;
                read += n;
            }

            _inner = CreateInner(header, read);
        }

        private async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
        {
            if (_inner is not null)
                return;

            var header = new byte[4];
            var read = 0;
            while (read < header.Length)
            {
                var n = await _source.ReadAsync(header.AsMemory(read), cancellationToken);
                if (n == 0)
                    break;
                read += n;
            }

            _inner = CreateInner(header, read);
        }

        private Stream CreateInner(byte[] header, int read)
        {
            var prefixed = new PrefixedStream(header, read, _source, _leaveOpen);

            // An empty source means empty content. Legacy gzip wrote empty payloads (e.g. an empty
            // filetree or empty file) as a 0-byte blob — System.IO.Compression only emits the gzip
            // header on first write — so read those back as empty rather than a format error. New zstd
            // blobs always carry a non-empty frame, so this path is hit only for legacy empty content.
            if (read == 0)
                return prefixed;

            if (read >= ZstdMagic.Length && header.AsSpan(0, ZstdMagic.Length).SequenceEqual(ZstdMagic))
                return new DecompressionStream(prefixed, leaveOpen: false);

            // Legacy "+gzip" blob: hand the un-peeked stream to the gzip codec to decode.
            if (GZipCompressionService.IsGZipHeader(header.AsSpan(0, read)))
                return LegacyGzip.WrapForDecompression(prefixed, leaveOpen: false);

            throw new InvalidDataException("Unrecognized compression format; expected a zstd or gzip frame header.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_inner is not null)
                    _inner.Dispose();      // disposes the PrefixedStream, which disposes _source unless leaveOpen
                else if (!_leaveOpen)
                    _source.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (_inner is not null)
                await _inner.DisposeAsync();
            else if (!_leaveOpen)
                await _source.DisposeAsync();

            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// Read-only stream that first replays a small already-read prefix, then continues from the
    /// underlying source. Used to "un-peek" the magic bytes consumed during format detection.
    /// </summary>
    private sealed class PrefixedStream : Stream
    {
        private readonly byte[] _prefix;
        private readonly int _prefixLength;
        private int _prefixPosition;
        private readonly Stream _source;
        private readonly bool _leaveOpen;

        public PrefixedStream(byte[] prefix, int prefixLength, Stream source, bool leaveOpen)
        {
            _prefix = prefix;
            _prefixLength = prefixLength;
            _source = source;
            _leaveOpen = leaveOpen;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_prefixPosition < _prefixLength)
                return ReadFromPrefix(buffer);

            return _source.Read(buffer);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_prefixPosition < _prefixLength)
                return ValueTask.FromResult(ReadFromPrefix(buffer.Span));

            return _source.ReadAsync(buffer, cancellationToken);
        }

        private int ReadFromPrefix(Span<byte> buffer)
        {
            var n = Math.Min(buffer.Length, _prefixLength - _prefixPosition);
            _prefix.AsSpan(_prefixPosition, n).CopyTo(buffer);
            _prefixPosition += n;
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_leaveOpen)
                _source.Dispose();

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_leaveOpen)
                await _source.DisposeAsync();

            GC.SuppressFinalize(this);
        }
    }
}
