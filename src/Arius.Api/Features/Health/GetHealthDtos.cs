namespace Arius.Api.Features.Health;

/// <summary>The service status.</summary>
/// <param name="Status">Always <c>ok</c>: the service answered.</param>
public sealed record HealthDto(string Status);
