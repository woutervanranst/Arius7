using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;
using Arius.Core.Features.StorageAccountInfoQuery;
using Mediator;

namespace Arius.Api.Features.Repositories;

internal static class RepositoryEndpoints
{
    /// <summary>Builds a repository DTO, resolving its region through a read-through cache in the app database.</summary>
    internal static async Task<RepositoryDto> ResolveRegionDtoAsync(
        AppDatabase db,
        RepositoryRecord repository,
        Func<long, CancellationToken, Task<StorageAccountInfo?>> resolveLive,
        CancellationToken ct,
        DateTimeOffset? lastArchiveStartedAt = null)
    {
        if (repository.RegionHint is not null)
            return ToDto(db, repository, repository.RegionHint, regionIsDefault: false, lastArchiveStartedAt);

        var info = await resolveLive(repository.Id, ct);
        if (info is { RegionIsDefault: false })
            db.SetRepositoryRegionHint(repository.Id, info.Region); // only a configured region is immutable; a default one is resolved again
        return ToDto(db, repository, info?.Region, info?.RegionIsDefault ?? false, lastArchiveStartedAt);
    }

    /// <summary>
    /// Resolves a repository's storage-account info (the pricing region). Returns <c>null</c>, shown as unknown,
    /// when the container is unreachable or misconfigured, instead of failing the caller.
    /// </summary>
    internal static async Task<StorageAccountInfo?> TryGetAccountInfoAsync(RepositoryProviderRegistry registry, long repositoryId, CancellationToken ct)
    {
        try
        {
            var provider = await registry.GetReadProviderAsync(repositoryId, ct);
            return await provider.GetRequiredService<IMediator>().Send(new StorageAccountInfoQuery(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    internal static RepositoryDto ToDto(
        AppDatabase db,
        RepositoryRecord repository,
        string? region,
        bool regionIsDefault,
        DateTimeOffset? lastArchiveStartedAt = null)
    {
        var accountName = db.GetAccount(repository.AccountId)?.Name ?? "";
        return new RepositoryDto(
            repository.Id, repository.Alias, repository.Container, repository.AccountId, accountName,
            repository.LocalPath, db.ResolveRepositoryDefaults(repository.Id).Tier.Value,
            Region:          region,
            RegionIsDefault: regionIsDefault,
            LastArchiveStartedAt: lastArchiveStartedAt);
    }
}
