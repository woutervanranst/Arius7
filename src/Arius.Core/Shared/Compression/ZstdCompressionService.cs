using System.IO.Compression;

namespace Arius.Core.Shared.Compression;

/// <summary>
/// Writes checksummed RFC 8878 frames and reads Zstandard or legacy gzip payloads.
/// </summary>
[SharedWithinAssembly]
internal sealed class ZstdCompressionService(int compressionLevel = ZstdCompressionService.DefaultCompressionLevel)
    : ICompressionService
{
    private static ReadOnlySpan<byte> ZstdMagic => [0x28, 0xB5, 0x2F, 0xFD];

    private static readonly GZipCompressionService LegacyGzip = new();

    public const int DefaultCompressionLevel = 15;

    private const int MaxDecodeWindowLog2 = 27; // Cap the decoding window at 128 MiB to bound memory use.

    public Stream WrapForCompression(Stream destination, bool leaveOpen = true)
        => new ZstandardStream(destination, new ZstandardCompressionOptions
        {
            Quality = compressionLevel,
            AppendChecksum = true,
            WindowLog2 = 0, // Use the compression level's window size.
            EnableLongDistanceMatching = false,
        }, leaveOpen);

    public Stream WrapForDecompression(Stream source, bool leaveOpen = false)
        => new AutoDetectDecompressionStream(source, leaveOpen);

    /// <summary>
    /// Defers format detection until the first read to avoid blocking during construction.
    /// </summary>
    private sealed class AutoDetectDecompressionStream(Stream source, bool leaveOpen) : Stream
    {
        private          Stream? _inner;

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
            cancellationToken.ThrowIfCancellationRequested();
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
                var n = source.Read(header.AsSpan(read));
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
                var n = await source.ReadAsync(header.AsMemory(read), cancellationToken);
                if (n == 0)
                    break;
                read += n;
            }

            _inner = CreateInner(header, read);
        }

        private Stream CreateInner(byte[] header, int read)
        {
            var prefixed = new PrefixedStream(header, read, source, leaveOpen);

            // Legacy gzip payloads can be empty because GZipStream emits a header only after a write.
            if (read == 0)
                return prefixed;

            if (read >= ZstdMagic.Length && header.AsSpan(0, ZstdMagic.Length).SequenceEqual(ZstdMagic))
                return new ZstandardStream(prefixed, new ZstandardDecompressionOptions
                {
                    MaxWindowLog2 = MaxDecodeWindowLog2,
                }, leaveOpen: false);

            if (GZipCompressionService.IsGZipHeader(header.AsSpan(0, read)))
                return LegacyGzip.WrapForDecompression(prefixed, leaveOpen: false);

            throw new InvalidDataException("Unrecognized compression format; expected a zstd or gzip frame header.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_inner is not null)
                    _inner.Dispose();
                else if (!leaveOpen)
                    source.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (_inner is not null)
                await _inner.DisposeAsync();
            else if (!leaveOpen)
                await source.DisposeAsync();

            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// Replays the detection header so the decoder receives the complete frame.
    /// </summary>
    private sealed class PrefixedStream(byte[] prefix, int prefixLength, Stream source, bool leaveOpen)
        : Stream
    {
        private          int  _prefixPosition;

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
            if (_prefixPosition < prefixLength)
                return ReadFromPrefix(buffer);

            return source.Read(buffer);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_prefixPosition < prefixLength)
                return ValueTask.FromResult(ReadFromPrefix(buffer.Span));

            return source.ReadAsync(buffer, cancellationToken);
        }

        private int ReadFromPrefix(Span<byte> buffer)
        {
            var n = Math.Min(buffer.Length, prefixLength - _prefixPosition);
            prefix.AsSpan(_prefixPosition, n).CopyTo(buffer);
            _prefixPosition += n;
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !leaveOpen)
                source.Dispose();

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!leaveOpen)
                await source.DisposeAsync();

            GC.SuppressFinalize(this);
        }
    }
}
