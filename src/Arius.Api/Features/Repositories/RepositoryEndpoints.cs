using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;
using Arius.Core.Features.StorageAccountInfoQuery;
using Mediator;

namespace Arius.Api.Features.Repositories;

/// <summary>Helpers shared by the <c>/repos</c> endpoints.</summary>
internal static class RepositoryEndpoints
{
    /// <summary>
    /// Builds a repository DTO, resolving its region through a DB-backed read-through cache.
    /// </summary>
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
            db.SetRepositoryRegionHint(repository.Id, info.Region); // cache only a configured region (immutable); leave unset ones to re-resolve
        return ToDto(db, repository, info?.Region, info?.RegionIsDefault ?? false, lastArchiveStartedAt);
    }

    /// <summary>
    /// Resolves a repository's storage-account info (currently the pricing region) through Core's Mediator.
    /// Best-effort: a Mediator query needs the repo's read provider, and an unreachable or misconfigured
    /// container throws while that provider is built — degrade to <c>null</c> (rendered as unknown) rather
    /// than failing the caller.
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
