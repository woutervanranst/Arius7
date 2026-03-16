using System.Collections.Concurrent;
using System.Threading.Channels;
using Arius.Core.Infrastructure;
using Arius.Core.Infrastructure.Packing;
using Arius.Core.Models;

namespace Arius.Core.Application.Restore;

/// <summary>
/// 4-phase parallel restore pipeline:
///
///   Phase 1 – Plan:    load snapshot + index, collect unique PackIds → emit RestorePlanReady
///   Phase 2 – Fetch:   Parallel.ForEachAsync over packs → download → extract blobs to tempDir → emit RestorePackFetched
///   Phase 3 – Assemble: Parallel.ForEachAsync over files → read chunks from disk → verify HMAC → write output
///   Phase 4 – Cleanup: delete tempDir in finally
///
/// All progress / error events are written to a shared events channel that the
/// caller reads via <see cref="RunAsync"/>.
/// </summary>
internal sealed class RestorePipeline
{
    private readonly AzureRepository   _repo;
    private readonly byte[]            _masterKey;
    private readonly ParallelismOptions _opts;
    private readonly string            _targetPath;
    private readonly string            _tempDir;

    public RestorePipeline(
        AzureRepository   repo,
        byte[]            masterKey,
        ParallelismOptions opts,
        string            targetPath,
        string            tempDir)
    {
        _repo       = repo;
        _masterKey  = masterKey;
        _opts       = opts;
        _targetPath = targetPath;
        _tempDir    = tempDir;
    }

    // ── Public entry point ────────────────────────────────────────────────────

    public async IAsyncEnumerable<RestoreEvent> RunAsync(
        IReadOnlyList<BackupSnapshotFile> files,
        Dictionary<string, IndexEntry> index,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var events = Channel.CreateUnbounded<RestoreEvent>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = true,
        });

        var pipelineTask = RunPipelineAsync(files, index, events.Writer, ct);

        await foreach (var evt in events.Reader.ReadAllAsync(ct))
            yield return evt;

        await pipelineTask;
    }

    // ── Internal pipeline driver ──────────────────────────────────────────────

    private async Task RunPipelineAsync(
        IReadOnlyList<BackupSnapshotFile> files,
        Dictionary<string, IndexEntry> index,
        ChannelWriter<RestoreEvent> events,
        CancellationToken ct)
    {
        try
        {
            // ── Phase 1: Plan ─────────────────────────────────────────────────

            long totalBytes = files.Sum(f => f.Size);

            // Collect the unique PackIds required for these files
            var neededPackIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in files)
                foreach (var chunkHash in file.ChunkHashes)
                    if (index.TryGetValue(chunkHash.Value, out var entry))
                        neededPackIds.Add(entry.PackId.Value);

            events.TryWrite(new RestorePlanReady(files.Count, totalBytes, neededPackIds.Count));

            // ── Phase 2: Fetch packs to temp dir ─────────────────────────────

            Directory.CreateDirectory(_tempDir);

            await Parallel.ForEachAsync(
                neededPackIds,
                new ParallelOptions { MaxDegreeOfParallelism = _opts.MaxDownloaders, CancellationToken = ct },
                async (packIdStr, token) =>
                {
                    var packId    = new PackId(packIdStr);
                    var packBytes = await _repo.DownloadPackAsync(packId, token);
                    var (blobs, _) = await PackReader.ExtractAsync(packBytes, _masterKey, token);

                    // Write each blob to {tempDir}/{hash}.bin
                    foreach (var (hashValue, data) in blobs)
                    {
                        var blobPath = Path.Combine(_tempDir, $"{hashValue}.bin");
                        await File.WriteAllBytesAsync(blobPath, data, token);
                    }

                    events.TryWrite(new RestorePackFetched(packIdStr, blobs.Count));
                });

            // ── Phase 3: Assemble output files ────────────────────────────────

            Directory.CreateDirectory(_targetPath);

            long[] counters = new long[3];
            // [0] restoredFiles  [1] restoredBytes  [2] failed
            const int iRestored = 0; const int iBytes = 1; const int iFailed = 2;

            await Parallel.ForEachAsync(
                files,
                new ParallelOptions { MaxDegreeOfParallelism = _opts.MaxAssemblers, CancellationToken = ct },
                async (file, token) =>
                {
                    var relativePath = GetRelativePath(file.Path);
                    var outputPath   = Path.Combine(
                        _targetPath,
                        relativePath.TrimStart(Path.DirectorySeparatorChar, '/'));
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? _targetPath);

                    try
                    {
                        await using var outputStream = File.Create(outputPath);

                        foreach (var chunkHash in file.ChunkHashes)
                        {
                            if (!index.TryGetValue(chunkHash.Value, out _))
                                throw new InvalidOperationException(
                                    $"Blob not found in index for file: {file.Path} (hash: {chunkHash.Value})");

                            var blobPath = Path.Combine(_tempDir, $"{chunkHash.Value}.bin");
                            var chunkData = await File.ReadAllBytesAsync(blobPath, token);

                            // Verify HMAC integrity
                            var actualHash = BlobHash.FromBytes(chunkData, _masterKey);
                            if (actualHash != chunkHash)
                                throw new InvalidDataException(
                                    $"Integrity check failed for chunk {chunkHash.Value}: got {actualHash.Value}");

                            await outputStream.WriteAsync(chunkData, token);
                        }

                        Interlocked.Increment(ref counters[iRestored]);
                        Interlocked.Add(ref counters[iBytes], file.Size);
                        events.TryWrite(new RestoreFileRestored(file.Path, file.Size));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref counters[iFailed]);
                        events.TryWrite(new RestoreFileError(file.Path, ex.Message));
                    }
                });

            events.TryWrite(new RestoreCompleted(
                RestoredFiles: (int)Interlocked.Read(ref counters[iRestored]),
                RestoredBytes: Interlocked.Read(ref counters[iBytes]),
                Failed:        (int)Interlocked.Read(ref counters[iFailed])));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            events.TryComplete(ex);
            throw;
        }
        finally
        {
            // Phase 4: cleanup temp dir
            if (Directory.Exists(_tempDir))
            {
                try { Directory.Delete(_tempDir, recursive: true); }
                catch { /* best-effort cleanup */ }
            }

            events.TryComplete();
        }
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
