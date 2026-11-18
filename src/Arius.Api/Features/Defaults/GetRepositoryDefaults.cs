using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Defaults;

/// <summary><c>GET /repos/{id}/defaults</c></summary>
internal sealed class GetRepositoryDefaults(AppDatabase db) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/repos/{id:long}/defaults", (long id, GetRepositoryDefaults endpoint) => endpoint.Handle(id));

    public IResult Handle(long id) =>
        db.GetRepository(id) is null ? Results.NotFound() : Results.Ok(DefaultsEndpoints.ToRepositoryDto(db, id));
}
