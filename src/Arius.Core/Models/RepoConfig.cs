namespace Arius.Core.Models;

public sealed record RepoConfig(
    RepoId RepoId,
    int Version,
    int GearSeed,
    long PackSize,
    int ChunkMin,
    int ChunkAvg,
    int ChunkMax);
