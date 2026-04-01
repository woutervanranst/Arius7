namespace Arius.Core.Shared.Storage;

/// <summary>
/// Controls which preflight probe is executed before returning a container-scoped blob service.
/// </summary>
public enum PreflightMode
{
    /// <summary>
    /// Calls <c>container.ExistsAsync()</c>. Used by restore and ls.
    /// </summary>
    ReadOnly,

    /// <summary>
    /// Uploads and deletes a probe blob. Used by archive.
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
    /// Returns a validated container-scoped service for <paramref name="containerName"/>.
    /// </summary>
    Task<IBlobContainerService> GetContainerServiceAsync(
        string containerName,
        PreflightMode preflightMode,
        CancellationToken cancellationToken = default);
}
