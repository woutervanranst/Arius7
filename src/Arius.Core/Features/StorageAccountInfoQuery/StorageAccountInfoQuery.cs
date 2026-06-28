using Arius.Core.Shared.Cost;
using Arius.Core.Shared.Storage;
using Mediator;

namespace Arius.Core.Features.StorageAccountInfoQuery;

// --- QUERY

/// <summary>
/// Mediator query: facts about a repository's backing storage account/container that are derived from the
/// storage itself rather than the app database — currently the pricing region. Cheap: it reads the region
/// the provider already resolved when it was built and performs no blob I/O of its own.
/// </summary>
public sealed record StorageAccountInfoQuery() : IQuery<StorageAccountInfo>;

// --- RESULT

/// <summary>Information about a repository's backing storage account/container.</summary>
/// <param name="Region">
/// The region the storage cost is priced for: the container's configured region, or the storage provider's
/// fallback default when the container has no region metadata set.
/// </param>
/// <param name="RegionIsDefault">
/// <c>true</c> when <see cref="Region"/> is the provider's fallback default (the container has no region
/// metadata set) — a hint that it should be configured for accurate pricing.
/// </param>
public sealed record StorageAccountInfo(string Region, bool RegionIsDefault);

// --- HANDLER

/// <summary>
/// Resolves the region entirely through Core abstractions: the cost estimator reports the resolved region
/// (the provider default stays sealed inside the storage adapter), and a null/blank
/// <see cref="IBlobContainerService.RegionHint"/> means the container carries no region metadata.
/// </summary>
public sealed class StorageAccountInfoQueryHandler(
    IBlobContainerService container,
    IStorageCostEstimator costEstimator)
    : IQueryHandler<StorageAccountInfoQuery, StorageAccountInfo>
{
    public ValueTask<StorageAccountInfo> Handle(StorageAccountInfoQuery query, CancellationToken cancellationToken)
        => ValueTask.FromResult(new StorageAccountInfo(
            Region:          costEstimator.Region,
            RegionIsDefault: string.IsNullOrWhiteSpace(container.RegionHint)));
}
