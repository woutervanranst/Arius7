using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Accounts;

/// <summary><c>DELETE /accounts/{id}</c></summary>
internal sealed class DeleteAccount(AppDatabase db) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapDelete("/accounts/{id:long}", (long id, DeleteAccount endpoint) => endpoint.Handle(id));

    public IResult Handle(long id)
    {
        if (db.GetAccount(id) is null)
            return Results.NotFound();
        if (db.CountRepositoriesForAccount(id) > 0)
            return Results.Conflict("Account still has repositories.");

        db.DeleteAccount(id);
        return Results.NoContent();
    }
}
