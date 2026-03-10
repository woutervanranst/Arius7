namespace Arius.Core.Application.Abstractions;

public interface IRequest<out TResponse>;

public interface IRequestHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    ValueTask<TResponse> Handle(TRequest request, CancellationToken cancellationToken = default);
}

public interface IStreamRequest<out TItem>;

public interface IStreamRequestHandler<in TRequest, TItem>
    where TRequest : IStreamRequest<TItem>
{
    IAsyncEnumerable<TItem> Handle(TRequest request, CancellationToken cancellationToken = default);
}
