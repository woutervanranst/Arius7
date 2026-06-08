namespace Arius.Core.Shared.Storage;

/// <summary>
/// Controls which preflight probe is executed before returning a container-scoped blob service.
/// </summary>
public enum PreflightMode
{
    /// <summary>
    /// Fetches one page of blobs to probe list permission. Used by restore and ls.
    /// </summary>
    ReadOnly,

    /// <summary>
    /// Creates the container if absent, then uploads and deletes a probe blob to validate write access. Used by archive.
    /// </summary>
    ReadWrite,
}

/// <summary>
/// Abstracts account-level blob operations. Arius.Core depends on this interface only.
/// </summary>
public interface IBlobService
{
    /// <summary>
    /// Lists repository container names available in the backing account.
    /// </summary>
    IAsyncEnumerable<string> GetContainerNamesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a validated container-scoped service for <paramref name="containerName"/>. Not a pure getter:
    /// in <see cref="PreflightMode.ReadWrite"/> it creates the container if absent, then probes write access;
    /// in <see cref="PreflightMode.ReadOnly"/> it requires the container to exist and probes list access.
    /// Throws <see cref="PreflightException"/> on credential/access/not-found failures.
    /// </summary>
    Task<IBlobContainerService> OpenContainerServiceAsync(
        string containerName,
        PreflightMode preflightMode,
        CancellationToken cancellationToken = default);
}
