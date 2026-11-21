using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Downloads;

internal sealed class UpdateDownloadSettings(AppDatabase db) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPut("/settings/downloads", (DownloadSettings settings, UpdateDownloadSettings endpoint) => endpoint.Handle(settings))
            .WithSummary("Update the download clean-up settings")
            .WithDescription("Returns 400 Bad Request unless the clean-up delay is between 1 and 3650 days.");

    private IResult Handle(DownloadSettings settings)
    {
        if (settings.CleanupDays is < 1 or > 3650)
            return Results.BadRequest("Clean-up days must be between 1 and 3650.");

        db.SaveDownloadSettings(settings);
        return Results.Ok(settings);
    }
}
