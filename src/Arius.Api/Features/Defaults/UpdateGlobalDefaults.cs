using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Defaults;

internal sealed class UpdateGlobalDefaults(AppDatabase db) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPut("/settings/defaults", (DefaultValues values, UpdateGlobalDefaults endpoint) => endpoint.Handle(values))
            .WithSummary("Replace the global defaults")
            .WithDescription("Replaces all global defaults. A null option is unset and falls back to its factory value. Returns 400 Bad Request for an unknown tier or on-disk value.");

    private IResult Handle(DefaultValues values)
    {
        if (OptionDefaults.Validate(values) is { } error)
            return Results.BadRequest(error);

        db.ReplaceGlobalDefaults(values);
        return Results.Ok(DefaultsEndpoints.ToGlobalDto(db));
    }
}
