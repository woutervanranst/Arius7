using Arius.Api.Shared.AppData;

namespace Arius.Api.Features.Defaults;

/// <summary>A repository's own defaults and what it would inherit for each option if it set none.</summary>
/// <param name="Set">Options the repository sets itself; <c>null</c> means inherited.</param>
/// <param name="Inherited">For each option, the value and its source that the repository inherits when it does not set the option.</param>
public sealed record RepositoryDefaultsDto(DefaultValues Set, ResolvedDefaults Inherited);
