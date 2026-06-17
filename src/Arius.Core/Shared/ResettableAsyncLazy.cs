namespace Arius.Core.Shared;

/// <summary>
/// A single-value async cache: like <see cref="AsyncLazy{T}"/> it computes its value once and shares it across
/// concurrent callers, but unlike <see cref="AsyncLazy{T}"/> (resolve-once, never reset) it can be
/// <see cref="Reset"/> so the next access recomputes, and it replaces a faulted or cancelled attempt instead of
/// pinning it — so a transient failure does not poison the value for the rest of its lifetime.
/// <para>
/// The factory runs under <see cref="CancellationToken.None"/>, detached from any caller. Each caller observes
/// only its own token via <see cref="GetAsync"/>, so one caller cancelling never fails the value other callers
/// share. Use for a lazily-loaded value that can go stale and must be re-loaded.
/// </para>
/// </summary>
internal sealed class ResettableAsyncLazy<T>
{
    private readonly Func<CancellationToken, Task<T>> _factory;
    private readonly Lock                             _gate = new();
    private          Task<T>?                         _task;

    public ResettableAsyncLazy(Func<CancellationToken, Task<T>> factory) => _factory = factory;

    /// <summary>
    /// Returns the cached value, computing it on first use and recomputing it after a faulted/cancelled attempt
    /// or a <see cref="Reset"/>. The returned task is cancelled per <paramref name="cancellationToken"/> without
    /// affecting the shared computation or other callers.
    /// </summary>
    public async Task<T> GetAsync(CancellationToken cancellationToken = default)
    {
        Task<T> task;
        lock (_gate)
        {
            if (_task is null || _task.IsFaulted || _task.IsCanceled)
                _task = _factory(CancellationToken.None);
            task = _task;
        }

        return await task.WaitAsync(cancellationToken);
    }

    /// <summary>Drops the cached value so the next <see cref="GetAsync"/> recomputes it.</summary>
    public void Reset()
    {
        lock (_gate)
            _task = null;
    }
}
