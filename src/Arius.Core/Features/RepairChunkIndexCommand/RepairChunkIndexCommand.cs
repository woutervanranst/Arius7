using Arius.Core.Shared.ChunkIndex;
using Mediator;

namespace Arius.Core.Features.RepairChunkIndexCommand;

public sealed record RepairChunkIndexCommand : ICommand<RepairChunkIndexResult>;

public sealed record RepairChunkIndexResult
{
    public required bool Success { get; init; }
    public ChunkIndexRepairResult? Repair { get; init; }
    public string? ErrorMessage { get; init; }
}
