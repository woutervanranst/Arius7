using Arius.Api.Composition;
using Arius.Api.Contracts;
using Arius.Api.AppData;
using Arius.Core.Features.StorageAccountInfoQuery;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Api.Endpoints;

/// <summary>Repository CRUD over the app database. Browsing/snapshots/stats endpoints are added in later phases.</summary>
internal static class RepositoryEndpoints
{
    public static void MapRepositoryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/repos");

        group.MapGet("/", async (AppDatabase db, RepositoryProviderRegistry registry, CancellationToken ct) =>
        {
            // The region is derived from the container (the source of truth) via Core's StorageAccountInfoQuery,
            // resolved per repo and concurrently so one slow/unreachable container can't stall the list.
            var repositories = db.ListRepositories();
            var infos        = await Task.WhenAll(repositories.Select(r => TryGetAccountInfoAsync(registry, r.Id, ct)));
            return repositories.Zip(infos, (r, info) => ToDto(db, r, info)).ToList();
        });

        group.MapGet("/{id:long}", async (long id, AppDatabase db, RepositoryProviderRegistry registry, CancellationToken ct) =>
        {
            var repository = db.GetRepository(id);
            if (repository is null)
                return Results.NotFound();
            var info = await TryGetAccountInfoAsync(registry, id, ct);
            return Results.Ok(ToDto(db, repository, info));
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
                NormalizeTier(request.DefaultTier),
                secrets.Protect(request.Passphrase));

            // Region is left unresolved here (the container may not exist yet); the client refetches the list,
            // which resolves it. Building a read provider for a brand-new repo would only cache a failed open.
            return Results.Created($"/repos/{id}", ToDto(db, db.GetRepository(id)!, info: null));
        });

        group.MapPatch("/{id:long}", (long id, UpdateRepositoryRequest request, AppDatabase db, SecretProtector secrets, RepositoryProviderRegistry registry) =>
        {
            if (db.GetRepository(id) is null)
                return Results.NotFound();

            db.UpdateRepository(
                id,
                request.Alias,
                request.LocalPath,
                request.DefaultTier is null ? null : NormalizeTier(request.DefaultTier),
                secrets.Protect(request.Passphrase));

            // Connection material may have changed — drop the cached read provider so it rebuilds,
            // and discard any memoized statistics (they may have been computed against a different target).
            registry.Evict(id);
            db.ClearStatisticsCache(id);
            // Region is left unresolved here; the client refetches the list, which resolves it against the
            // (now evicted) provider.
            return Results.Ok(ToDto(db, db.GetRepository(id)!, info: null));
        });

        group.MapDelete("/{id:long}", (long id, AppDatabase db, RepositoryProviderRegistry registry) =>
        {
            if (db.GetRepository(id) is null)
                return Results.NotFound();

            db.DeleteRepository(id);
            registry.Remove(id); // repo is gone for good → also dispose its rolling-log factory, not just Evict the provider
            return Results.NoContent();
        });
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

    private static RepositoryDto ToDto(AppDatabase db, RepositoryRecord repository, StorageAccountInfo? info)
    {
        var accountName = db.GetAccount(repository.AccountId)?.Name ?? "";
        return new RepositoryDto(
            repository.Id, repository.Alias, repository.Container, repository.AccountId, accountName,
            repository.LocalPath, repository.DefaultTier,
            Region:          info?.Region,
            RegionIsDefault: info?.RegionIsDefault ?? false);
    }

    private static string NormalizeTier(string? tier)
        => string.IsNullOrWhiteSpace(tier) ? "archive" : tier.Trim().ToLowerInvariant();
}
