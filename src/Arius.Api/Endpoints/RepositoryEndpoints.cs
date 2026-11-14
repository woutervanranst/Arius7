using Arius.Api.AppData;
using Arius.Api.Composition;
using Arius.Api.Contracts;
using Arius.Core.Features.StorageAccountInfoQuery;
using Mediator;

namespace Arius.Api.Endpoints;

/// <summary>Repository CRUD over the app database.</summary>
internal static class RepositoryEndpoints
{
    public static void MapRepositoryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/repos");

        group.MapGet("/", async (AppDatabase db, RepositoryProviderRegistry registry, CancellationToken ct) =>
        {
            var repositories = db.ListRepositories();
            var lastArchiveStarts = db.ListLatestArchiveStartedAt();
            var dtos = await Task.WhenAll(repositories.Select(r =>
                ResolveRegionDtoAsync(
                    db, r, (id, c) => TryGetAccountInfoAsync(registry, id, c), ct,
                    lastArchiveStarts.TryGetValue(r.Id, out var startedAt) ? startedAt : null)));
            return dtos.ToList();
        });

        group.MapGet("/{id:long}", async (long id, AppDatabase db, RepositoryProviderRegistry registry, CancellationToken ct) =>
        {
            var repository = db.GetRepository(id);
            if (repository is null)
                return Results.NotFound();
            var lastArchiveStarts = db.ListLatestArchiveStartedAt(id);
            return Results.Ok(await ResolveRegionDtoAsync(
                db, repository, (rid, c) => TryGetAccountInfoAsync(registry, rid, c), ct,
                lastArchiveStarts.TryGetValue(id, out var startedAt) ? startedAt : null));
        });

        group.MapPost("/", (CreateRepositoryRequest request, AppDatabase db, SecretProtector secrets) =>
        {
            if (db.GetAccount(request.AccountId) is null)
                return Results.BadRequest($"Account {request.AccountId} does not exist.");

            var id = db.InsertRepository(
                request.Alias,
                request.Container,
                request.AccountId,
                request.LocalPath,
                secrets.Protect(request.Passphrase));

            // Region is left unresolved here (the container may not exist yet); the client refetches the list, which resolves and caches it.
            return Results.Created($"/repos/{id}", ToDto(db, db.GetRepository(id)!, region: null, regionIsDefault: false));
        });

        group.MapPatch("/{id:long}", (long id, UpdateRepositoryRequest request, AppDatabase db, SecretProtector secrets, RepositoryProviderRegistry registry) =>
        {
            if (db.GetRepository(id) is null)
                return Results.NotFound();

            db.UpdateRepository(
                id,
                request.Alias,
                request.LocalPath,
                secrets.Protect(request.Passphrase));

            // Connection material may have changed — drop the cached read provider so it rebuilds, discard any
            // memoized statistics, and invalidate the region cache so it re-resolves against the (possibly new) target.
            registry.Evict(id);
            db.ClearStatisticsCache(id);
            db.SetRepositoryRegionHint(id, null);
            // Region is left unresolved here; the client refetches the list, which re-resolves and caches it.
            var lastArchiveStarts = db.ListLatestArchiveStartedAt(id);
            return Results.Ok(ToDto(
                db, db.GetRepository(id)!, region: null, regionIsDefault: false,
                lastArchiveStarts.TryGetValue(id, out var startedAt) ? startedAt : null));
        });

        group.MapDelete("/{id:long}", (long id, AppDatabase db, RepositoryProviderRegistry registry) =>
        {
            if (db.GetRepository(id) is null)
                return Results.NotFound();

            // Deleting disposes the repo's logger factory, which a concurrent job build resolves loggers from.
            if (db.HasActiveJob(id))
                return Results.Conflict("Repository has an active job; wait for it to finish or cancel it before deleting.");

            db.DeleteRepository(id);
            registry.Remove(id); // repo is gone for good → also dispose its rolling-log factory, not just Evict the provider
            return Results.NoContent();
        });
    }

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
    private static async Task<StorageAccountInfo?> TryGetAccountInfoAsync(RepositoryProviderRegistry registry, long repositoryId, CancellationToken ct)
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

    private static RepositoryDto ToDto(
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
