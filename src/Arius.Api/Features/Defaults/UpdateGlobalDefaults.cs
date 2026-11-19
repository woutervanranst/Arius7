using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Defaults;

/// <summary><c>PUT /settings/defaults</c></summary>
internal sealed class UpdateGlobalDefaults(AppDatabase db) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPut("/settings/defaults", (DefaultValues values, UpdateGlobalDefaults endpoint) => endpoint.Handle(values));

    private IResult Handle(DefaultValues values)
    {
        if (OptionDefaults.Validate(values) is { } error)
            return Results.BadRequest(error);

        db.ReplaceGlobalDefaults(values);
        return Results.Ok(DefaultsEndpoints.ToGlobalDto(db));
    }
}
