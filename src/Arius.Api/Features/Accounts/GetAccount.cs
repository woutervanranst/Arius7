using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Accounts;

/// <summary><c>GET /accounts/{id}</c></summary>
internal sealed class GetAccount(AppDatabase db) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/accounts/{id:long}", (long id, GetAccount endpoint) => endpoint.Handle(id));

    private IResult Handle(long id)
    {
        var account = db.GetAccount(id);
        return account is null ? Results.NotFound() : Results.Ok(AccountEndpoints.ToDto(db, account));
    }
}
