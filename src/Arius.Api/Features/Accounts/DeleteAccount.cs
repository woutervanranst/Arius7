using Arius.Api.AppData;

namespace Arius.Api.Endpoints;

/// <summary><c>DELETE /accounts/{id}</c></summary>
internal static class DeleteAccount
{
    public static void Map(IEndpointRouteBuilder group) =>
        group.MapDelete("/{id:long}", (long id, AppDatabase db) =>
        {
            if (db.GetAccount(id) is null)
                return Results.NotFound();
            if (db.CountRepositoriesForAccount(id) > 0)
                return Results.Conflict("Account still has repositories.");

            db.DeleteAccount(id);
            return Results.NoContent();
        });
}
