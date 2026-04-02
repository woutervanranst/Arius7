using Mediator;

namespace Arius.Core.Features.List;

/// <summary>
/// Mediator stream query: list repository entries in a snapshot.
/// </summary>
public sealed record ListQuery(ListQueryOptions Options) : IStreamQuery<RepositoryEntry>;