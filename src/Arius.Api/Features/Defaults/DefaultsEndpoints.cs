using Arius.Api.Shared.AppData;

namespace Arius.Api.Features.Defaults;

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
