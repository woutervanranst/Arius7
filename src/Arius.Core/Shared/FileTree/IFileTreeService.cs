namespace Arius.Core.Shared.FileTree;

/// <summary>
/// Public contract for the two-tier filetree cache. Feature handlers depend on this interface
/// so the concrete <see cref="FileTreeService"/> implementation can stay internal to Arius.Core.
/// </summary>
public interface IFileTreeService
{
    /// <summary>
    /// Returns the persisted filetree entries for the given <paramref name="hash"/>.
    /// </summary>
    Task<IReadOnlyList<FileTreeEntry>> ReadAsync(FileTreeHash hash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads the tree entries to Azure (if not already present) and writes the plaintext
    /// representation to the local disk cache.
    /// </summary>
    Task WriteAsync((FileTreeHash Hash, ReadOnlyMemory<byte> Plaintext) payload, CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads the tree entries only if the filetree blob is not already present in the remote.
    /// </summary>
    Task EnsureStoredAsync((FileTreeHash Hash, ReadOnlyMemory<byte> Plaintext) payload, CancellationToken cancellationToken = default);

    /// <summary>
    /// Compares the latest local snapshot with the latest remote snapshot, materializing remote
    /// filetree markers on mismatch. Must be called once before <see cref="ExistsInRemote"/>.
    /// </summary>
    Task<FileTreeValidationResult> ValidateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns <c>true</c> if the filetree blob for the given <paramref name="hash"/> exists in the
    /// remote (or is already cached locally). Must be called after <see cref="ValidateAsync"/>.
    /// </summary>
    bool ExistsInRemote(FileTreeHash hash);
}
