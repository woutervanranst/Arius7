using Arius.Api.Shared.AppData;

namespace Arius.Api.Features.Accounts;

/// <summary><c>GET /accounts/{id}</c></summary>
internal static class GetAccount
{
    public static void Map(IEndpointRouteBuilder group) =>
        group.MapGet("/{id:long}", (long id, AppDatabase db) =>
        {
            var account = db.GetAccount(id);
            return account is null ? Results.NotFound() : Results.Ok(AccountEndpoints.ToDto(db, account));
        });
}
