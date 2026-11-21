using Mediator;

namespace Arius.Api.FakeTestHost;

/// <summary>
/// Stand-ins for the Mediator commands/queries the scripted harness doesn't script.
/// </summary>
/// <remarks>
/// An unscripted request throws <see cref="NotSupportedException"/> instead of failing to resolve the real handler,
/// whose Core services the scripted composer does not register.
/// </remarks>
internal sealed class NotConfiguredCommandHandler<TCommand, TResponse> : ICommandHandler<TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    public ValueTask<TResponse> Handle(TCommand command, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"Scripted test harness: no scenario registered for {typeof(TCommand).Name}.");
}

internal sealed class NotConfiguredQueryHandler<TQuery, TResponse> : IQueryHandler<TQuery, TResponse>
    where TQuery : IQuery<TResponse>
{
    public ValueTask<TResponse> Handle(TQuery query, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"Scripted test harness: no scenario registered for {typeof(TQuery).Name}.");
}

internal sealed class NotConfiguredStreamQueryHandler<TQuery, TResponse> : IStreamQueryHandler<TQuery, TResponse>
    where TQuery : IStreamQuery<TResponse>
{
    public IAsyncEnumerable<TResponse> Handle(TQuery query, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"Scripted test harness: no scenario registered for {typeof(TQuery).Name}.");
}
