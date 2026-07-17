using Mediator;

namespace Arius.Api.FakeTestHost;

/// <summary>
/// Stand-ins for the Mediator commands/queries the scripted harness doesn't script.
/// </summary>
/// <remarks>
/// Othamar Mediator's generated <c>ContainerMetadata</c> eagerly resolves <i>every</i> discovered
/// <see cref="ICommandHandler{TCommand,TResponse}"/>/<see cref="IQueryHandler{TQuery,TResponse}"/>/
/// <see cref="IStreamQueryHandler{TQuery,TResponse}"/> on the first <c>Send</c>/<c>Publish</c> call — not
/// just the one the caller actually invoked. In production, <c>AddArius()</c>'s explicit factories are
/// registered after — and so shadow — <c>AddMediator()</c>'s auto-registrations, which resolve the concrete
/// handler via constructor injection and would otherwise fail: the constructors need Core services the
/// scripted composer never registers, or raw <c>accountName</c>/<c>containerName</c> strings DI cannot supply.
/// The scripted composer never calls <c>AddArius()</c>, so these stand-ins take over that shadowing role
/// for every command/query a scenario doesn't script. <c>Handle</c> always throws — none are ever invoked.
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
