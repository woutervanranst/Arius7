using Arius.Api.Shared.AppData;

namespace Arius.Api.Features.Defaults;

/// <summary><c>GET /repos/{id}/defaults</c></summary>
internal static class GetRepositoryDefaults
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/repos/{id:long}/defaults", (long id, AppDatabase db) =>
            db.GetRepository(id) is null ? Results.NotFound() : Results.Ok(DefaultsEndpoints.ToRepositoryDto(db, id)));
}
