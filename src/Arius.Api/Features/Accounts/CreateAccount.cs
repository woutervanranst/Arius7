using Arius.Api.Shared.AppData;

namespace Arius.Api.Features.Accounts;

/// <summary><c>POST /accounts</c></summary>
internal static class CreateAccount
{
    public static void Map(IEndpointRouteBuilder group) =>
        group.MapPost("/", (CreateAccountRequest request, AppDatabase db, SecretProtector secrets) =>
        {
            var id = db.InsertAccount(request.Name, secrets.Protect(request.AccountKey));
            var account = db.GetAccount(id)!;
            return Results.Created($"/accounts/{id}", AccountEndpoints.ToDto(db, account));
        });
}
