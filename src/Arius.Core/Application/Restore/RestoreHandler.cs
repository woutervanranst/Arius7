using Arius.Core.Application.Abstractions;
using Arius.Core.Infrastructure;
using Arius.Core.Infrastructure.Packing;
using Arius.Core.Models;

namespace Arius.Core.Application.Restore;

public sealed class RestoreHandler : IStreamRequestHandler<RestoreRequest, RestoreEvent>
{
    private readonly Func<string, string, AzureRepository> _repoFactory;

    public RestoreHandler(Func<string, string, AzureRepository> repoFactory)
        => _repoFactory = repoFactory;

    public async IAsyncEnumerable<RestoreEvent> Handle(
        RestoreRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var repo      = _repoFactory(request.ConnectionString, request.ContainerName);
        var masterKey = await repo.UnlockAsync(request.Passphrase, cancellationToken);

        // Load snapshot
        var doc   = await repo.LoadSnapshotDocumentAsync(request.SnapshotId, cancellationToken);
        var files = (IReadOnlyList<BackupSnapshotFile>)doc.Files;

        if (!string.IsNullOrEmpty(request.Include))
        {
            files = files
                .Where(f => f.Path.Contains(request.Include, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var totalBytes = files.Sum(f => f.Size);
        yield return new RestorePlanReady(files.Count, totalBytes);

        // Load index once
        var index = await repo.LoadIndexAsync(cancellationToken);

        // Pack cache: packId → blob map (loaded from Azure, in memory — no disk staging)
        var packCache = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.Ordinal);

        Directory.CreateDirectory(request.TargetPath);

        long restoredBytes = 0;
        int restoredFiles  = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = GetRelativePath(file.Path);
            var outputPath   = Path.Combine(
                request.TargetPath,
                relativePath.TrimStart(Path.DirectorySeparatorChar, '/'));
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? request.TargetPath);

            await using var outputStream = File.Create(outputPath);

            foreach (var chunkHash in file.ChunkHashes)
            {
                if (!index.TryGetValue(chunkHash.Value, out var entry))
                    throw new InvalidOperationException(
                        $"Blob not found in index for file: {file.Path} (hash: {chunkHash.Value})");

                var packIdStr = entry.PackId.Value;
                if (!packCache.TryGetValue(packIdStr, out var packBlobs))
                {
                    // Download pack from Azure into memory — no filesystem staging (D14)
                    var packBytes = await repo.DownloadPackAsync(entry.PackId, cancellationToken);
                    var (blobs, _) = await PackReader.ExtractAsync(packBytes, masterKey, cancellationToken);
                    packBlobs = blobs;
                    packCache[packIdStr] = packBlobs;
                }

                if (!packBlobs.TryGetValue(chunkHash.Value, out var chunkData))
                    throw new InvalidDataException(
                        $"Chunk '{chunkHash.Value}' not found in pack '{packIdStr}'.");

                // Verify HMAC integrity
                var actualHash = BlobHash.FromBytes(chunkData, masterKey);
                if (actualHash != chunkHash)
                    throw new InvalidDataException(
                        $"Integrity check failed for chunk {chunkHash.Value}: got {actualHash.Value}");

                await outputStream.WriteAsync(chunkData, cancellationToken);
            }

            restoredFiles++;
            restoredBytes += file.Size;
            yield return new RestoreFileRestored(file.Path, file.Size);
        }

        yield return new RestoreCompleted(restoredFiles, restoredBytes);
    }

    private static string GetRelativePath(string absolutePath)
    {
        if (!Path.IsPathRooted(absolutePath))
            return absolutePath;

        var parts = absolutePath.Replace('\\', '/').Split('/');
        return parts.Length >= 2
            ? string.Join(Path.DirectorySeparatorChar.ToString(), parts[^2..])
            : parts[^1];
    }
}
