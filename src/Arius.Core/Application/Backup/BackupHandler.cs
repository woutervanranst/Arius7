using Arius.Core.Application.Abstractions;
using Arius.Core.Infrastructure;
using Arius.Core.Infrastructure.Chunking;
using Arius.Core.Infrastructure.Packing;
using Arius.Core.Models;

namespace Arius.Core.Application.Backup;

public sealed class BackupHandler : IStreamRequestHandler<BackupRequest, BackupEvent>
{
    private readonly Func<string, string, AzureRepository> _repoFactory;

    public BackupHandler(Func<string, string, AzureRepository> repoFactory)
        => _repoFactory = repoFactory;

    public async IAsyncEnumerable<BackupEvent> Handle(
        BackupRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var repo      = _repoFactory(request.ConnectionString, request.ContainerName);
        var masterKey = await repo.UnlockAsync(request.Passphrase, cancellationToken);
        var config    = await repo.LoadConfigAsync(cancellationToken);
        var chunker   = GearChunker.FromConfig(config);

        var files = ExpandFiles(request.Paths).ToList();
        yield return new BackupStarted(files.Count);

        // Load existing index for dedup
        var existingIndex = await repo.LoadIndexAsync(cancellationToken);

        var snapshotFiles = new List<BackupSnapshotFile>(files.Count);
        var newEntries    = new List<IndexEntry>();
        var seenThisRun   = new HashSet<string>(StringComparer.Ordinal);

        int stored      = 0;
        int deduplicated = 0;

        // Helper: upload a sealed pack directly to Azure (no filesystem)
        async Task UploadSealedPack(SealedPack sp)
        {
            await repo.UploadPackAsync(sp, request.DataTier, cancellationToken);
            newEntries.AddRange(sp.IndexEntries);
        }

        await using var packer = new PackerManager(masterKey, config.PackSize);

        foreach (var filePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var info        = new FileInfo(filePath);
            var chunkHashes = new List<BlobHash>();
            int newChunksThisFile = 0;

            await using var fileStream = File.OpenRead(filePath);
            await foreach (var chunk in chunker.ChunkAsync(fileStream, cancellationToken))
            {
                var chunkBytes = chunk.Data.ToArray();
                var blobHash   = BlobHash.FromBytes(chunkBytes, masterKey);
                chunkHashes.Add(blobHash);

                bool alreadyKnown =
                    existingIndex.ContainsKey(blobHash.Value) ||
                    seenThisRun.Contains(blobHash.Value);

                if (alreadyKnown) continue;

                newChunksThisFile++;
                seenThisRun.Add(blobHash.Value);

                var blob     = new BlobToPack(blobHash, BlobType.Data, chunkBytes);
                var sealed_  = await packer.AddAsync(blob, cancellationToken);
                if (sealed_ is not null)
                    await UploadSealedPack(sealed_);
            }

            if (newChunksThisFile == 0)
                deduplicated++;
            else
                stored++;

            snapshotFiles.Add(new BackupSnapshotFile(info.FullName, chunkHashes, info.Length));
            yield return new BackupFileProcessed(info.FullName, info.Length, newChunksThisFile == 0);
        }

        // Flush remaining blobs
        var flushed = await packer.FlushAsync(cancellationToken);
        if (flushed is not null)
            await UploadSealedPack(flushed);

        // Write snapshot to Azure (Cold tier)
        var snapshot = new Snapshot(
            SnapshotId.New(),
            DateTimeOffset.UtcNow,
            TreeHash.Empty,
            request.Paths,
            Environment.MachineName,
            Environment.UserName,
            Array.Empty<string>(),
            null);

        var doc = new BackupSnapshotDocument(snapshot, snapshotFiles);
        await repo.WriteSnapshotAsync(doc, cancellationToken);

        // Write index delta (Cold tier)
        if (newEntries.Count > 0)
            await repo.WriteIndexAsync(snapshot.Id, newEntries, cancellationToken);

        yield return new BackupCompleted(snapshot, stored, deduplicated);
    }

    private static IEnumerable<string> ExpandFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                yield return Path.GetFullPath(path);
                continue;
            }

            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    yield return Path.GetFullPath(file);
            }
        }
    }
}
