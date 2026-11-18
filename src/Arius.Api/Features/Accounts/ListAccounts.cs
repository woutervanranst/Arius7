using Arius.Api.Shared.AppData;

namespace Arius.Api.Features.Accounts;

/// <summary><c>GET /accounts</c></summary>
internal static class ListAccounts
{
    public static void Map(IEndpointRouteBuilder group) =>
        group.MapGet("/", (AppDatabase db) =>
            db.ListAccounts().Select(a => AccountEndpoints.ToDto(db, a)).ToList());
}
