using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Arius.Api.Features.Accounts;

internal sealed class GetAccount(AppDatabase db) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/accounts/{id:long}", (long id, GetAccount endpoint) => endpoint.Handle(id))
            .WithSummary("Get a storage account")
            .WithDescription("Returns 404 Not Found if the account does not exist.");

    private Results<Ok<AccountDto>, NotFound> Handle(long id)
    {
        var account = db.GetAccount(id);
        return account is null ? TypedResults.NotFound() : TypedResults.Ok(AccountEndpoints.ToDto(db, account));
    }
}
