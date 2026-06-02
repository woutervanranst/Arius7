using Arius.Core.Shared.ChunkIndex;
using Mediator;
using Microsoft.Extensions.Logging;

namespace Arius.Core.Features.RepairChunkIndexCommand;

public sealed class RepairChunkIndexCommandHandler(
    ChunkIndexService index,
    ILogger<RepairChunkIndexCommandHandler> logger,
    string accountName,
    string containerName)
    : ICommandHandler<RepairChunkIndexCommand, RepairChunkIndexResult>
{
    public async ValueTask<RepairChunkIndexResult> Handle(RepairChunkIndexCommand command, CancellationToken cancellationToken)
    {
        logger.LogInformation("[repair] Start: account={Account} container={Container}", accountName, containerName);

        try
        {
            logger.LogInformation("[phase] repair-index");
            var result = await index.RepairAsync(cancellationToken);
            logger.LogInformation("[repair] Done: listed={ListedChunks} rebuiltEntries={RebuiltEntries} rebuiltShards={RebuiltShards} uploadedShards={UploadedShards} deletedStaleShards={DeletedStaleShards}", result.ListedChunkCount, result.RebuiltEntryCount, result.RebuiltShardCount, result.UploadedShardCount, result.DeletedStaleShardCount);
            return new RepairChunkIndexResult { Success = true, Repair = result };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[repair] Failure");
            return new RepairChunkIndexResult { Success = false, ErrorMessage = ex.Message };
        }
    }
}
