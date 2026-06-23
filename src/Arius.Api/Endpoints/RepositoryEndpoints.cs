using Arius.Api.Composition;
using Arius.Api.Contracts;
using Arius.Api.AppData;

namespace Arius.Api.Endpoints;

/// <summary>Repository CRUD over the app database. Browsing/snapshots/stats endpoints are added in later phases.</summary>
internal static class RepositoryEndpoints
{
    public static void MapRepositoryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/repos");

        group.MapGet("/", (AppDatabase db) =>
            db.ListRepositories().Select(r => ToDto(db, r)).ToList());

        group.MapGet("/{id:long}", (long id, AppDatabase db) =>
        {
            var repository = db.GetRepository(id);
            return repository is null ? Results.NotFound() : Results.Ok(ToDto(db, repository));
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

            return Results.Created($"/repos/{id}", ToDto(db, db.GetRepository(id)!));
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
            return Results.Ok(ToDto(db, db.GetRepository(id)!));
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

    private static RepositoryDto ToDto(AppDatabase db, RepositoryRecord repository)
    {
        var accountName = db.GetAccount(repository.AccountId)?.Name ?? "";
        return new RepositoryDto(repository.Id, repository.Alias, repository.Container, repository.AccountId, accountName, repository.LocalPath, repository.DefaultTier);
    }

    private static string NormalizeTier(string? tier)
        => string.IsNullOrWhiteSpace(tier) ? "archive" : tier.Trim().ToLowerInvariant();
}
