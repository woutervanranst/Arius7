namespace Arius.Core.Shared.FileTree;

/// <summary>
/// Writes completed-file entries to an unsorted temp file during the archive pipeline.
/// Call <see cref="FlushAsync"/> / Dispose when done.
/// Thread-safe for concurrent writers.
/// </summary>
public sealed class ManifestWriter : IAsyncDisposable
{
    private readonly StreamWriter  _writer;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string TempFilePath { get; }

    public ManifestWriter(string tempFilePath)
    {
        TempFilePath = tempFilePath;
        _writer      = new StreamWriter(
            new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true),
            System.Text.Encoding.UTF8,
            leaveOpen: false);
    }

    /// <summary>Appends a manifest entry. Thread-safe.</summary>
    public async Task AppendAsync(ManifestEntry entry, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await _writer.WriteLineAsync(entry.Serialize().AsMemory(), cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.FlushAsync();
        _writer.Dispose();
        _lock.Dispose();
    }
}