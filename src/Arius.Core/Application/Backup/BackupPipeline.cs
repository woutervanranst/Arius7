using System.Collections.Concurrent;
using System.Threading.Channels;
using Arius.Core.Application.Abstractions;
using Arius.Core.Infrastructure;
using Arius.Core.Infrastructure.Chunking;
using Arius.Core.Infrastructure.Packing;
using Arius.Core.Models;

namespace Arius.Core.Application.Backup;

/// <summary>
/// Channel-based 4-stage parallel backup pipeline.
///
/// Topology:
///   [File Processors (N)] → packingCh → [Accumulator (1)] → sealCh → [Seal Workers (M)] → uploadCh → [Uploaders (P)]
///
/// All workers write progress / error events to a shared unbounded events channel.
/// The outer <see cref="BackupHandler.Handle"/> reads from that channel and yields each event.
/// </summary>
internal sealed class BackupPipeline
{
    // Bounded channel capacities (backpressure)
    private const int PackingChannelCapacity = 128;
    private const int SealChannelCapacity    = 8;
    private const int UploadChannelCapacity  = 8;

    private readonly AzureRepository              _repo;
    private readonly byte[]                       _masterKey;
    private readonly RepoConfig                   _config;
    private readonly GearChunker                  _chunker;
    private readonly Dictionary<string, IndexEntry> _existingIndex;
    private readonly BlobAccessTier               _dataTier;
    private readonly ParallelismOptions           _opts;
    private readonly IReadOnlyList<string>        _requestPaths;

    public BackupPipeline(
        AzureRepository                  repo,
        byte[]                           masterKey,
        RepoConfig                       config,
        GearChunker                      chunker,
        Dictionary<string, IndexEntry>   existingIndex,
        BlobAccessTier                   dataTier,
        ParallelismOptions               opts,
        IReadOnlyList<string>            requestPaths)
    {
        _repo          = repo;
        _masterKey     = masterKey;
        _config        = config;
        _chunker       = chunker;
        _existingIndex = existingIndex;
        _dataTier      = dataTier;
        _opts          = opts;
        _requestPaths  = requestPaths;
    }

    // ── Public entry point ────────────────────────────────────────────────────

    /// <summary>
    /// Runs the pipeline over <paramref name="files"/> and yields <see cref="BackupEvent"/>s.
    /// </summary>
    public async IAsyncEnumerable<BackupEvent> RunAsync(
        IReadOnlyList<string> files,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var events = Channel.CreateUnbounded<BackupEvent>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = true,
        });

        // Run the pipeline as a background task; events arrive via the channel
        var pipelineTask = RunPipelineAsync(files, events.Writer, ct);

        // Yield every event as it arrives
        await foreach (var evt in events.Reader.ReadAllAsync(ct))
            yield return evt;

        // Propagate any pipeline exception
        await pipelineTask;
    }

    // ── Internal pipeline driver ──────────────────────────────────────────────

    private async Task RunPipelineAsync(
        IReadOnlyList<string> files,
        ChannelWriter<BackupEvent> events,
        CancellationToken ct)
    {
        try
        {
            // ── Inter-stage channels ──────────────────────────────────────────

            var packingCh = Channel.CreateBounded<BlobToPack>(
                new BoundedChannelOptions(PackingChannelCapacity)
                {
                    FullMode     = BoundedChannelFullMode.Wait,
                    SingleWriter = false,
                    SingleReader = true,
                });

            var sealCh = Channel.CreateBounded<List<BlobToPack>>(
                new BoundedChannelOptions(SealChannelCapacity)
                {
                    FullMode     = BoundedChannelFullMode.Wait,
                    SingleWriter = true,
                    SingleReader = false,
                });

            var uploadCh = Channel.CreateBounded<SealedPack>(
                new BoundedChannelOptions(UploadChannelCapacity)
                {
                    FullMode     = BoundedChannelFullMode.Wait,
                    SingleWriter = false,
                    SingleReader = false,
                });

            // ── Shared mutable state (thread-safe) ────────────────────────────

            var seenThisRun     = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            var snapshotFiles   = new ConcurrentBag<BackupSnapshotFile>();
            var newIndexEntries = new ConcurrentBag<IndexEntry>();

            // Atomic counters – use long[] so lambdas can close over them
            long[] counters = new long[9];
            //   [0] failed
            //   [1] totalChunks   [2] newChunks   [3] dedupChunks
            //   [4] totalBytes    [5] newBytes     [6] dedupBytes
            //   [7] storedFiles   [8] dedupFiles
            const int iFailed       = 0;
            const int iTotalChunks  = 1; const int iNewChunks  = 2; const int iDedupChunks  = 3;
            const int iTotalBytes   = 4; const int iNewBytes   = 5; const int iDedupBytes   = 6;
            const int iStoredFiles  = 7; const int iDedupFiles  = 8;

            // ── Stage 1: File processors (N workers) ─────────────────────────

            var fileProcessorsTask = Task.Run(async () =>
            {
                try
                {
                    await Parallel.ForEachAsync(
                        files,
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = _opts.MaxFileProcessors,
                            CancellationToken      = ct,
                        },
                        async (filePath, token) =>
                        {
                            var info = new FileInfo(filePath);
                            try
                            {
                                var chunkHashes  = new List<BlobHash>();
                                bool hasNewChunks = false;

                                await using var fileStream = File.OpenRead(filePath);
                                await foreach (var chunk in _chunker.ChunkAsync(fileStream, token))
                                {
                                    var chunkBytes = chunk.Data.ToArray();
                                    var blobHash   = BlobHash.FromBytes(chunkBytes, _masterKey);
                                    chunkHashes.Add(blobHash);

                                    Interlocked.Increment(ref counters[iTotalChunks]);
                                    Interlocked.Add(ref counters[iTotalBytes], chunkBytes.Length);

                                    // Dedup check: already in persistent index OR claimed this run
                                    if (_existingIndex.ContainsKey(blobHash.Value) ||
                                        !seenThisRun.TryAdd(blobHash.Value, 0))
                                    {
                                        Interlocked.Increment(ref counters[iDedupChunks]);
                                        Interlocked.Add(ref counters[iDedupBytes], chunkBytes.Length);
                                        continue;
                                    }

                                    // New chunk — claim & send to packing
                                    hasNewChunks = true;
                                    Interlocked.Increment(ref counters[iNewChunks]);
                                    Interlocked.Add(ref counters[iNewBytes], chunkBytes.Length);

                                    await packingCh.Writer.WriteAsync(
                                        new BlobToPack(blobHash, BlobType.Data, chunkBytes), token);
                                }

                                snapshotFiles.Add(new BackupSnapshotFile(info.FullName, chunkHashes, info.Length));
                                if (hasNewChunks)
                                    Interlocked.Increment(ref counters[iStoredFiles]);
                                else
                                    Interlocked.Increment(ref counters[iDedupFiles]);

                                events.TryWrite(new BackupFileProcessed(info.FullName, info.Length, !hasNewChunks));
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                Interlocked.Increment(ref counters[iFailed]);
                                events.TryWrite(new BackupFileError(filePath, ex.Message));
                            }
                        });
                }
                finally
                {
                    packingCh.Writer.Complete();
                }
            }, ct);

            // ── Stage 2: Pack accumulator (single consumer) ───────────────────

            var accumulatorTask = Task.Run(async () =>
            {
                try
                {
                    var pending      = new List<BlobToPack>();
                    long pendingSize = 0;
                    long threshold   = _config.PackSize;

                    await foreach (var blob in packingCh.Reader.ReadAllAsync(ct))
                    {
                        pending.Add(blob);
                        pendingSize += blob.Data.Length;

                        if (pendingSize >= threshold)
                        {
                            var batch = pending.ToList();
                            pending.Clear();
                            pendingSize = 0;
                            await sealCh.Writer.WriteAsync(batch, ct);
                        }
                    }

                    // Flush remaining
                    if (pending.Count > 0)
                        await sealCh.Writer.WriteAsync(pending, ct);
                }
                finally
                {
                    sealCh.Writer.Complete();
                }
            }, ct);

            // ── Stage 3: Seal workers (M workers) ─────────────────────────────

            var sealWorkersTask = Task.Run(async () =>
            {
                try
                {
                    var workers = Enumerable
                        .Range(0, _opts.MaxSealWorkers)
                        .Select(_ => Task.Run(async () =>
                        {
                            await foreach (var batch in sealCh.Reader.ReadAllAsync(ct))
                            {
                                var sealedPack = await PackerManager.SealAsync(batch, _masterKey, ct);
                                await uploadCh.Writer.WriteAsync(sealedPack, ct);
                            }
                        }, ct))
                        .ToArray();

                    await Task.WhenAll(workers);
                }
                finally
                {
                    uploadCh.Writer.Complete();
                }
            }, ct);

            // ── Stage 4: Uploaders (P workers) ───────────────────────────────

            var uploadersTask = Task.Run(async () =>
            {
                var workers = Enumerable
                    .Range(0, _opts.MaxUploaders)
                    .Select(_ => Task.Run(async () =>
                    {
                        await foreach (var sealedPack in uploadCh.Reader.ReadAllAsync(ct))
                        {
                            await _repo.UploadPackAsync(sealedPack, _dataTier, ct);
                            foreach (var entry in sealedPack.IndexEntries)
                                newIndexEntries.Add(entry);
                        }
                    }, ct))
                    .ToArray();

                await Task.WhenAll(workers);
            }, ct);

            // ── Wait for all stages ───────────────────────────────────────────

            await Task.WhenAll(fileProcessorsTask, accumulatorTask, sealWorkersTask, uploadersTask);

            // ── Write snapshot + index, emit BackupCompleted ──────────────────

            var snapshotFilesList = snapshotFiles.ToList();
            Snapshot? snapshot = null;

            // Only write snapshot if there were files (even if all failed, write what we have)
            if (snapshotFilesList.Count > 0)
            {
                snapshot = new Snapshot(
                    SnapshotId.New(),
                    DateTimeOffset.UtcNow,
                    TreeHash.Empty,
                    _requestPaths,
                    Environment.MachineName,
                    Environment.UserName,
                    [],
                    null);

                var doc = new BackupSnapshotDocument(snapshot, snapshotFilesList);
                await _repo.WriteSnapshotAsync(doc, ct);

                var entries = newIndexEntries.ToList();
                if (entries.Count > 0)
                    await _repo.WriteIndexAsync(snapshot.Id, entries, ct);
            }

            events.TryWrite(new BackupCompleted(
                Snapshot:           snapshot,
                StoredFiles:        (int)Interlocked.Read(ref counters[iStoredFiles]),
                DeduplicatedFiles:  (int)Interlocked.Read(ref counters[iDedupFiles]),
                Failed:             (int)Interlocked.Read(ref counters[iFailed]),
                TotalChunks:        Interlocked.Read(ref counters[iTotalChunks]),
                NewChunks:          Interlocked.Read(ref counters[iNewChunks]),
                DeduplicatedChunks: Interlocked.Read(ref counters[iDedupChunks]),
                TotalBytes:         Interlocked.Read(ref counters[iTotalBytes]),
                NewBytes:           Interlocked.Read(ref counters[iNewBytes]),
                DeduplicatedBytes:  Interlocked.Read(ref counters[iDedupBytes])));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            events.TryComplete(ex);
            throw;
        }
        finally
        {
            events.TryComplete();
        }
    }
}
