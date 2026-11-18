using Arius.Api.AppData;

namespace Arius.Api.Endpoints;

/// <summary><c>GET /repos/{id}/defaults</c></summary>
internal static class GetRepositoryDefaults
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/repos/{id:long}/defaults", (long id, AppDatabase db) =>
            db.GetRepository(id) is null ? Results.NotFound() : Results.Ok(DefaultsEndpoints.ToRepositoryDto(db, id)));
}
