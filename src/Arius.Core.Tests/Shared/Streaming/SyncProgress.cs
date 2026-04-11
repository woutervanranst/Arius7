namespace Arius.Core.Tests.Shared.Streaming;

/// <summary>Synchronous IProgress implementation for deterministic testing.</summary>
internal sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}
