using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Arius.Api.Features.Defaults;

/// <summary><c>PUT /settings/defaults</c></summary>
internal sealed class UpdateGlobalDefaults(AppDatabase db) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPut("/settings/defaults", (DefaultValues values, UpdateGlobalDefaults endpoint) => endpoint.Handle(values))
            .WithSummary("Replace the global defaults")
            .WithDescription("Replaces all global defaults. A null option is unset and falls back to its factory value. Returns 400 Bad Request for an unknown tier or on-disk value.");

    private Results<Ok<GlobalDefaultsDto>, BadRequest<string>> Handle(DefaultValues values)
    {
        if (OptionDefaults.Validate(values) is { } error)
            return TypedResults.BadRequest(error);

        db.ReplaceGlobalDefaults(values);
        return TypedResults.Ok(DefaultsEndpoints.ToGlobalDto(db));
    }
}
