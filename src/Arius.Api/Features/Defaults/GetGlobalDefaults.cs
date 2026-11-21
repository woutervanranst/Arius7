using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Defaults;

/// <summary><c>GET /settings/defaults</c></summary>
internal sealed class GetGlobalDefaults(AppDatabase db) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/settings/defaults", (GetGlobalDefaults endpoint) => endpoint.Handle())
            .WithSummary("Get the global defaults")
            .WithDescription("Returns the archive and restore option defaults set for the whole server, the factory values that apply when an option is not set, and for each option the repositories that inherit it.");

    private GlobalDefaultsDto Handle() => DefaultsEndpoints.ToGlobalDto(db);
}
