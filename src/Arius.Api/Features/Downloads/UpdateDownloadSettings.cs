using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Downloads;

/// <summary><c>PUT /settings/downloads</c></summary>
internal sealed class UpdateDownloadSettings(AppDatabase db) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPut("/settings/downloads", (DownloadSettings settings, UpdateDownloadSettings endpoint) => endpoint.Handle(settings));

    public IResult Handle(DownloadSettings settings)
    {
        if (settings.CleanupDays is < 1 or > 3650)
            return Results.BadRequest("Clean-up days must be between 1 and 3650.");

        db.SaveDownloadSettings(settings);
        return Results.Ok(settings);
    }
}
