using Arius.Api.AppData;

namespace Arius.Api.Contracts;

/// <summary>A repository's own defaults and what it would inherit for each option if it set none.</summary>
public sealed record RepositoryDefaultsDto(DefaultValues Set, ResolvedDefaults Inherited);
