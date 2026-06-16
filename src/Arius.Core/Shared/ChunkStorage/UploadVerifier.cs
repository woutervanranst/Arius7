using Arius.Core.Shared.Compression;
using Arius.Core.Shared.Encryption;
using System.IO.Pipelines;

namespace Arius.Core.Shared.ChunkStorage;

/// <summary>
/// Verifies (or, for trusted codecs, doesn't) that an uploading chunk round-trips. Compressed bytes are
/// tee'd into <see cref="Sink"/> as they stream to blob storage; <see cref="CompleteAsync"/> returns the
/// hash the stored chunk decompresses to, or <c>null</c> when this verifier does not verify.
/// </summary>
internal interface IUploadVerifier : IAsyncDisposable
{
    /// <summary>The tee's secondary target: compressed bytes written here are verified (or discarded).</summary>
    Stream Sink { get; }

    /// <summary>Finalizes verification; returns the restored hash to compare, or <c>null</c> when not verified.</summary>
    Task<ContentHash?> CompleteAsync(CancellationToken cancellationToken);
}


/// <summary>
/// A verifier for codecs Arius already trusts to round-trip (the BCL gzip path): it discards the tee'd bytes
/// and reports no hash, so the upload records the chunk without an inline check.
/// </summary>
internal sealed class NoopVerifier : IUploadVerifier
{
    public Stream             Sink                                               => Stream.Null;
    public Task<ContentHash?> CompleteAsync(CancellationToken cancellationToken) => Task.FromResult<ContentHash?>(null);
    public ValueTask          DisposeAsync()                                     => ValueTask.CompletedTask;
}


/// <summary>
/// Decompresses the compressed bytes written to <see cref="Sink"/> on a background task and hashes
/// the result, so an upload can confirm the chunk round-trips before recording it. A bounded pipe
/// supplies backpressure so memory stays flat regardless of chunk size.
/// </summary>
internal sealed class RoundTripVerifier : IUploadVerifier
{
    private const int PauseWriterThreshold = 1 << 20; // ≤ ~1 MiB of compressed bytes buffered in-flight
    private const int ResumeWriterThreshold = 1 << 19;

    private readonly Pipe _pipe;
    private readonly Task<ContentHash> _hashTask;
    private bool _writerCompleted;

    public RoundTripVerifier(ICompressionService compression, IEncryptionService encryption, CancellationToken cancellationToken)
    {
        _pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: PauseWriterThreshold,
            resumeWriterThreshold: ResumeWriterThreshold,
            useSynchronizationContext: false));

        Sink = _pipe.Writer.AsStream(leaveOpen: true); // the writer is completed explicitly, not via Sink disposal

        _hashTask = Task.Run(async () =>
        {
            await using var reader = _pipe.Reader.AsStream();
            await using var decompress = compression.WrapForDecompression(reader, leaveOpen: true);
            return await encryption.ComputeHashAsync(decompress, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>The tee's secondary target: compressed bytes written here are decompressed and hashed.</summary>
    public Stream Sink { get; }

    public async Task<ContentHash?> CompleteAsync(CancellationToken cancellationToken)
    {
        await CompleteWriterAsync();
        return await _hashTask.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        // Release both ends so an early-exit/faulted path can't leave the background task hanging.
        await CompleteWriterAsync();
        try { await _hashTask; } catch { /* the real failure is surfaced by CompleteAsync */ }
        await _pipe.Reader.CompleteAsync();
    }

    private async ValueTask CompleteWriterAsync()
    {
        if (_writerCompleted)
            return;

        _writerCompleted = true;
        await _pipe.Writer.CompleteAsync();
    }
}

/// <summary>
/// Write-only stream that forwards every write to two destinations: the primary (encrypt→upload)
/// and the round-trip verifier's sink. Lets us verify the compressed output while it streams to
/// blob storage, without re-reading the source or buffering the whole chunk. Both targets are left
/// open — the caller disposes the primary (for byte counting) and the verifier owns the sink.
/// </summary>
internal sealed class TeeStream(Stream primary, Stream secondary) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        primary.Write(buffer);
        secondary.Write(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await primary.WriteAsync(buffer, cancellationToken);
        await secondary.WriteAsync(buffer, cancellationToken);
    }

    public override void Flush()
    {
        primary.Flush();
        secondary.Flush();
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        await primary.FlushAsync(cancellationToken);
        await secondary.FlushAsync(cancellationToken);
    }
}