using Arius.Api.AppData;

namespace Arius.Api.Endpoints;

/// <summary><c>GET /accounts</c></summary>
internal static class ListAccounts
{
    public static void Map(IEndpointRouteBuilder group) =>
        group.MapGet("/", (AppDatabase db) =>
            db.ListAccounts().Select(a => AccountEndpoints.ToDto(db, a)).ToList());
}
