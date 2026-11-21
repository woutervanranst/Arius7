using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Accounts;

internal sealed class CreateAccount(AppDatabase db, SecretProtector secrets) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/accounts/", (CreateAccountRequest request, CreateAccount endpoint) => endpoint.Handle(request))
            .WithSummary("Add a storage account")
            .WithDescription("Saves a storage account and returns it with 201 Created. The account key is encrypted at rest.");

    private IResult Handle(CreateAccountRequest request)
    {
        var id = db.InsertAccount(request.Name, secrets.Protect(request.AccountKey));
        var account = db.GetAccount(id)!;
        return Results.Created($"/accounts/{id}", AccountEndpoints.ToDto(db, account));
    }
}
