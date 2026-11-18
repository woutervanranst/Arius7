using Arius.Api.Shared.AppData;

namespace Arius.Api.Features.Defaults;

/// <summary>A repository's own defaults and what it would inherit for each option if it set none.</summary>
public sealed record RepositoryDefaultsDto(DefaultValues Set, ResolvedDefaults Inherited);
