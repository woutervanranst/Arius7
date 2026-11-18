using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Downloads;

/// <summary><c>GET /settings/downloads</c></summary>
internal sealed class GetDownloadSettings(AppDatabase db) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/settings/downloads", (GetDownloadSettings endpoint) => endpoint.Handle());

    public IResult Handle() => Results.Ok(db.GetDownloadSettings());
}
