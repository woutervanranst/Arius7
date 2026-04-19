namespace Arius.E2E.Tests.Fixtures;

internal sealed record E2EBackendCapabilities(
    bool SupportsArchiveTier,
    bool SupportsRehydrationPlanning);
