using Arius.Api.Shared.AppData;

namespace Arius.Api.Features.Defaults;

/// <summary>Helpers shared by the <c>/settings/defaults</c> and <c>/repos/{id}/defaults</c> endpoints.</summary>
internal static class DefaultsEndpoints
{
    internal static GlobalDefaultsDto ToGlobalDto(AppDatabase db) => new(
        db.GetGlobalDefaults(),
        OptionDefaults.Factory,
        OptionDefaults.OptionNames.ToDictionary(option => option, db.ListAliasesInheriting));

    internal static RepositoryDefaultsDto ToRepositoryDto(AppDatabase db, long id) => new(
        db.GetRepositoryDefaults(id),
        OptionDefaults.Resolve(db.GetGlobalDefaults(), DefaultValues.None));
}
