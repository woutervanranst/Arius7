using Arius.Core.Shared;

namespace Arius.Core.Tests.Shared;

public class ResettableAsyncLazyTests
{
    [Test]
    public async Task GetAsync_CachesValue_FactoryRunsOnce()
    {
        var calls = 0;
        var lazy = new ResettableAsyncLazy<int>(_ => { calls++; return Task.FromResult(5); });

        (await lazy.GetAsync()).ShouldBe(5);
        (await lazy.GetAsync()).ShouldBe(5);

        calls.ShouldBe(1);
    }

    [Test]
    public async Task Reset_RecomputesOnNextGet()
    {
        var calls = 0;
        var lazy = new ResettableAsyncLazy<int>(_ => Task.FromResult(++calls));

        (await lazy.GetAsync()).ShouldBe(1);
        lazy.Reset();
        (await lazy.GetAsync()).ShouldBe(2);

        calls.ShouldBe(2);
    }

    [Test]
    public async Task GetAsync_FaultedFactory_IsRetriedOnNextGet()
    {
        var attempts = 0;
        var lazy = new ResettableAsyncLazy<int>(_ =>
        {
            attempts++;
            return attempts == 1
                ? Task.FromException<int>(new InvalidOperationException("boom"))
                : Task.FromResult(7);
        });

        await Should.ThrowAsync<InvalidOperationException>(async () => await lazy.GetAsync());
        (await lazy.GetAsync()).ShouldBe(7); // the faulted attempt was not pinned

        attempts.ShouldBe(2);
    }

    [Test]
    public async Task GetAsync_OneCallerCancelling_DoesNotFailOtherCallers()
    {
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lazy = new ResettableAsyncLazy<int>(_ => release.Task);

        using var cts = new CancellationTokenSource();
        var cancelledCaller = lazy.GetAsync(cts.Token);
        var liveCaller = lazy.GetAsync(CancellationToken.None);

        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(async () => await cancelledCaller);

        release.SetResult(42);
        (await liveCaller).ShouldBe(42); // the shared value survived the other caller's cancellation
    }
}
