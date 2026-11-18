using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Accounts;

/// <summary><c>GET /accounts</c></summary>
internal sealed class ListAccounts(AppDatabase db) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/accounts/", (ListAccounts endpoint) => endpoint.Handle());

    public List<AccountDto> Handle() =>
        db.ListAccounts().Select(a => AccountEndpoints.ToDto(db, a)).ToList();
}
