using Arius.Api.AppData;
using Arius.Api.Contracts;

namespace Arius.Api.Endpoints;

/// <summary>Global and per-repository archive/restore option defaults.</summary>
internal static class DefaultsEndpoints
{
    public static void MapDefaultsEndpoints(this IEndpointRouteBuilder app)
    {
        GetGlobalDefaults.Map(app);
        UpdateGlobalDefaults.Map(app);
        GetRepositoryDefaults.Map(app);
        UpdateRepositoryDefaults.Map(app);
    }

    internal static GlobalDefaultsDto ToGlobalDto(AppDatabase db) => new(
        db.GetGlobalDefaults(),
        OptionDefaults.Factory,
        OptionDefaults.OptionNames.ToDictionary(option => option, db.ListAliasesInheriting));

    internal static RepositoryDefaultsDto ToRepositoryDto(AppDatabase db, long id) => new(
        db.GetRepositoryDefaults(id),
        OptionDefaults.Resolve(db.GetGlobalDefaults(), DefaultValues.None));
}
