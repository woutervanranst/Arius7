using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Arius.Api.Features.Accounts;

/// <summary><c>DELETE /accounts/{id}</c></summary>
internal sealed class DeleteAccount(AppDatabase db) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapDelete("/accounts/{id:long}", (long id, DeleteAccount endpoint) => endpoint.Handle(id))
            .WithSummary("Delete a storage account")
            .WithDescription("Returns 409 Conflict while repositories still use the account, and 404 Not Found if it does not exist.");

    private Results<NoContent, NotFound, Conflict<string>> Handle(long id)
    {
        if (db.GetAccount(id) is null)
            return TypedResults.NotFound();
        if (db.CountRepositoriesForAccount(id) > 0)
            return TypedResults.Conflict("Account still has repositories.");

        db.DeleteAccount(id);
        return TypedResults.NoContent();
    }
}
