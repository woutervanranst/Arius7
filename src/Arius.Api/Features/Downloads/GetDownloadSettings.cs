using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Downloads;

internal sealed class GetDownloadSettings(AppDatabase db) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/settings/downloads", (GetDownloadSettings endpoint) => endpoint.Handle())
            .WithSummary("Get the download clean-up settings");

    private IResult Handle() => Results.Ok(db.GetDownloadSettings());
}
