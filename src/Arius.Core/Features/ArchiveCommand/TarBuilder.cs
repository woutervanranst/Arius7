using System.Formats.Tar;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Extensions;

namespace Arius.Core.Features.ArchiveCommand;

/// <summary>
/// Accumulates small files into content-addressed tar bundles for the archive pipeline's tar-build stage.
/// It exists to lift the stateful "open a bundle, append entries, seal at the target size" logic out of the
/// command handler — where it was a closure-captured tangle of mutable state and cleanup try/finally blocks —
/// with responsibility for writing tar entries named by content hash and sealing a bundle into an in-memory
/// <see cref="SealedTar"/> once it reaches the target size.
///
/// It is deliberately decoupled from the mediator and from any logging vocabulary: the three lifecycle moments
/// are surfaced as optional callbacks, so the caller (the command handler) owns event publishing and its own
/// log format. The caller also owns the source files (it opens them and decides how to skip unreadable ones)
/// and the output channel. A bundle still open when the builder is disposed (mid-build fault or cancellation)
/// is discarded by <see cref="DisposeAsync"/>.
/// </summary>
internal sealed class TarBuilder : IAsyncDisposable
{
    private readonly long               _targetSize;
    private readonly IEncryptionService _encryption;

    private readonly Func<ValueTask>?                         _onBundleStarted;
    private readonly Func<ContentHash, int, long, ValueTask>? _onEntryAdded;
    private readonly Func<SealedTar, ValueTask>?              _onBundleSealing;

    private readonly List<TarEntry> _entries = [];
    private TarWriter?    _tarWriter;
    private MemoryStream? _tarStream;
    private long          _currentSize;

    /// <param name="targetSize">A bundle is sealed once its accumulated size reaches this threshold.</param>
    /// <param name="encryption">Used to hash the sealed tar body.</param>
    /// <param name="onBundleStarted">Invoked when a new bundle is opened (its first entry).</param>
    /// <param name="onEntryAdded">Invoked after each entry is written, with (content hash, entry count, current bundle size).</param>
    /// <param name="onBundleSealing">Invoked when a bundle is sealed, before it is returned to the caller.</param>
    public TarBuilder(
        long                                     targetSize,
        IEncryptionService                       encryption,
        Func<ValueTask>?                         onBundleStarted = null,
        Func<ContentHash, int, long, ValueTask>? onEntryAdded    = null,
        Func<SealedTar, ValueTask>?              onBundleSealing = null)
    {
        _targetSize      = targetSize;
        _encryption      = encryption;
        _onBundleStarted = onBundleStarted;
        _onEntryAdded    = onEntryAdded;
        _onBundleSealing = onBundleSealing;
    }

    /// <summary>
    /// Writes one already-opened source file into the current bundle (opening a new bundle on demand), and
    /// returns the bundle as a <see cref="SealedTar"/> when this entry pushes it to the target size — otherwise
    /// <c>null</c>. The <paramref name="source"/> stream is disposed by this call.
    /// </summary>
    public async Task<SealedTar?> AddAsync(FileToUpload upload, Stream source, CancellationToken cancellationToken)
    {
        // Open a new bundle on demand. Capture the writer in a local so it is unambiguously non-null below.
        TarWriter writer;
        if (_tarWriter is null)
        {
            _tarStream = new MemoryStream();
            writer     = _tarWriter = new TarWriter(_tarStream, leaveOpen: true);
            await (_onBundleStarted?.Invoke() ?? ValueTask.CompletedTask);
        }
        else
        {
            writer = _tarWriter;
        }

        // Write the entry named by content-hash (not original path).
        var tarEntry = new PaxTarEntry(TarEntryType.RegularFile, upload.HashedPair.ContentHash.ToString());
        await using (source)
        {
            tarEntry.DataStream = source;
            await writer.WriteEntryAsync(tarEntry, cancellationToken);
        }
        tarEntry.DataStream = null;

        _entries.Add(new TarEntry(upload.HashedPair.ContentHash, upload.FileSize, upload.HashedPair));
        _currentSize += upload.FileSize;

        await (_onEntryAdded?.Invoke(upload.HashedPair.ContentHash, _entries.Count, _currentSize) ?? ValueTask.CompletedTask);

        return _currentSize >= _targetSize ? await SealAsync(cancellationToken) : null;
    }

    /// <summary>
    /// Seals the current bundle and returns it, or <c>null</c> when no bundle is open. After sealing, the
    /// builder is reset and ready to accumulate the next bundle.
    /// </summary>
    public async Task<SealedTar?> SealAsync(CancellationToken cancellationToken)
    {
        if (_tarWriter is null)
            return null;

        // Finalize the tar archive (writes the trailing blocks), then take its body.
        await _tarWriter.DisposeAsync();
        _tarWriter = null;

        var body = _tarStream!; // set together with _tarWriter in AddAsync, so non-null whenever the writer was
        body.Position = 0;
        var tarHash = ChunkHash.Parse(await _encryption.ComputeHashAsync(body, cancellationToken));

        // Hand the buffer off to the SealedTar. The MemoryStream wrapper holds no unmanaged resources and its
        // backing array outlives it (it is now owned by the segment), so the wrapper is simply abandoned.
        var sealedTar = new SealedTar(body.ToArraySegment(), tarHash, _currentSize, _entries.ToList());

        await (_onBundleSealing?.Invoke(sealedTar) ?? ValueTask.CompletedTask);

        _entries.Clear();
        _tarStream   = null;
        _currentSize = 0;

        return sealedTar;
    }

    public async ValueTask DisposeAsync()
    {
        // Discard any bundle still open (mid-build fault or cancellation).
        if (_tarWriter is not null)
            await _tarWriter.DisposeAsync();
        if (_tarStream is not null)
            await _tarStream.DisposeAsync();

        _tarWriter = null;
        _tarStream = null;
    }
}
