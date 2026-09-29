namespace Arius.Core.Tests.Shared.Streaming;

/// <summary>A clock that never advances, so every throttle window stays open.</summary>
internal sealed class FrozenTimeProvider : TimeProvider
{
    public override long GetTimestamp() => 0;
}
