## 1. Models & Contracts

- [ ] 1.1 Add `ParallelismOptions` record to `Arius.Core/Models/` with per-stage concurrency limits and `Default` static property
- [ ] 1.2 Add `BackupFileError(string Path, string Error)` event to `BackupContracts.cs`
- [ ] 1.3 Extend `BackupCompleted` with `Failed`, `TotalChunks`, `NewChunks`, `DeduplicatedChunks`, `TotalBytes`, `NewBytes`, `DeduplicatedBytes` fields
- [ ] 1.4 Add `Parallelism` property to `BackupRequest`
- [ ] 1.5 Add `RestoreFileError(string Path, string Error)` and `RestorePackFetched(string PackId, int BlobCount)` events to `RestoreContracts.cs`
- [ ] 1.6 Extend `RestoreCompleted` with `Failed` field
- [ ] 1.7 Extend `RestorePlanReady` with `PacksToDownload` field
- [ ] 1.8 Add `Parallelism` and `TempPath` properties to `RestoreRequest`

## 2. Infrastructure — Parallel Index Loading

- [ ] 2.1 Rewrite `AzureRepository.LoadIndexAsync` to collect blob names first, then `Parallel.ForEachAsync` with `ConcurrentBag<IndexEntry[]>`, then merge single-threaded
- [ ] 2.2 Add unit test for parallel index loading (multiple index blobs, verify merged result)

## 3. Backup Pipeline — Core Implementation

- [ ] 3.1 Create `BackupPipeline` internal class with the 4-stage Channel-based pipeline: file processors → pack accumulator → seal workers → uploaders
- [ ] 3.2 Implement file processor workers: open file → GearChunker.ChunkAsync → BlobHash.FromBytes → dedup via `ConcurrentDictionary.TryAdd` → write to packing channel. Accumulate `BackupSnapshotFile` in `ConcurrentBag`. Track chunk/byte counters with `Interlocked`.
- [ ] 3.3 Implement pack accumulator (single consumer): read from packing channel → feed PackerManager → on seal, write batch to seal channel
- [ ] 3.4 Implement seal workers: read batches → `PackerManager.SealAsync` (static, stateless) → write to upload channel
- [ ] 3.5 Implement uploaders: read SealedPacks → `repo.UploadPackAsync` → collect IndexEntries in `ConcurrentBag`
- [ ] 3.6 Implement event channel bridging: internal `Channel<BackupEvent>` written by all workers, `Handle` method reads and `yield return`s
- [ ] 3.7 Implement collect-and-report: per-file try/catch in file processors, emit `BackupFileError`, increment `Failed` counter, continue
- [ ] 3.8 Wire pipeline completion: `events.Writer.Complete()` after all stages finish, propagate exceptions via `Task.WhenAll`
- [ ] 3.9 Rewrite `BackupHandler.Handle` to use the new pipeline, applying `ParallelismOptions` from request
- [ ] 3.10 Handle edge cases: empty file list, all files deduplicated, single-file backup, cancellation token propagation

## 4. Restore Pipeline — Core Implementation

- [ ] 4.1 Implement restore Phase 1 (plan): load snapshot + index, scan all chunk hashes to collect unique PackIds, emit `RestorePlanReady` with `PacksToDownload`
- [ ] 4.2 Implement restore Phase 2 (fetch): `Parallel.ForEachAsync` over needed PackIds with `MaxDownloaders` — download pack → `PackReader.ExtractAsync` → write blobs to `{tempDir}/{hash}.bin` → release pack memory → emit `RestorePackFetched`
- [ ] 4.3 Implement restore Phase 3 (assemble): `Parallel.ForEachAsync` over files with `MaxAssemblers` — read chunks from temp dir → verify HMAC → write output file → emit `RestoreFileRestored`
- [ ] 4.4 Implement restore Phase 4 (cleanup): delete temp dir in `finally` block
- [ ] 4.5 Implement event channel bridging for restore (same pattern as backup)
- [ ] 4.6 Implement collect-and-report for restore: per-file try/catch, emit `RestoreFileError`, continue
- [ ] 4.7 Rewrite `RestoreHandler.Handle` to use the new phased pipeline with `ParallelismOptions` and `TempPath`

## 5. CLI — Humanizer & New Events

- [ ] 5.1 Add `Humanizer.Core` NuGet package to `Arius.Cli.csproj`
- [ ] 5.2 Replace `FormatBytes` in `BackupCommand.cs` with Humanizer `.Bytes().Humanize()` calls
- [ ] 5.3 Replace `FormatBytes` in `RestoreCommand.cs` with Humanizer formatting
- [ ] 5.4 Add Humanizer formatting for durations, relative timestamps, and quantities across all commands (ForgetCommand, PruneCommand, SnapshotsCommand)
- [ ] 5.5 Update `BackupCommand.cs` to handle `BackupFileError` events (display in red) and show chunk-level dedup stats from `BackupCompleted`
- [ ] 5.6 Update `RestoreCommand.cs` to handle `RestoreFileError` and `RestorePackFetched` events, show failed count
- [ ] 5.7 Add `--parallelism` option to backup and restore commands, wire to `ParallelismOptions`

## 6. API / Web — New Event Handling

- [ ] 6.1 Update API event DTOs/SignalR to handle `BackupFileError`, `RestoreFileError`, `RestorePackFetched` events and extended completion fields

## 7. Existing Tests — Update for New Contracts

- [ ] 7.1 Update `RepositoryWorkflowTests.cs` backup assertions for new `BackupCompleted` fields (chunk counters, `Failed`)
- [ ] 7.2 Update `RepositoryWorkflowTests.cs` restore assertions for new `RestoreCompleted` fields and `RestorePlanReady.PacksToDownload`
- [ ] 7.3 Update `WorkflowIntegrationTests.cs` for new event types and fields
- [ ] 7.4 Verify all existing tests pass with the parallel pipeline

## 8. Concurrency Tests — Deterministic & Stress (TUnit)

- [ ] 8.1 Create `tests/Arius.Core.Tests/Concurrency/DedupGateTests.cs`: barrier-based test with 10 threads calling TryAdd on same hash, assert exactly 1 wins
- [ ] 8.2 Create `tests/Arius.Core.Tests/Concurrency/DedupGateTests.cs`: test that duplicate blob across two files results in exactly one pack entry
- [ ] 8.3 Create `tests/Arius.Core.Tests/Concurrency/BackupPipelineStressTests.cs`: 1000 files with ~30% content overlap, high parallelism backup + restore + byte-level verification
- [ ] 8.4 Create `tests/Arius.Core.Tests/Concurrency/BackupPipelineStressTests.cs`: error collection test — files that become unreadable mid-operation, verify BackupFileError events and continued processing
- [ ] 8.5 Create `tests/Arius.Core.Tests/Concurrency/RestorePipelineStressTests.cs`: parallel pack download + assembly, verify all files byte-identical
- [ ] 8.6 Create `tests/Arius.Core.Tests/Concurrency/RestorePipelineStressTests.cs`: verify temp directory cleanup after successful and failed restores
- [ ] 8.7 Add index uniqueness assertion to all parallel backup tests

## 9. Concurrency Tests — Coyote (Separate Project)

- [ ] 9.1 Create `tests/Arius.Coyote.Tests/Arius.Coyote.Tests.csproj` targeting `net8.0` with `Microsoft.Coyote` and `Microsoft.Coyote.Test` packages
- [ ] 9.2 Create dedup interleaving test: 4 workers claiming same blob, Coyote explores 1000 interleavings, assert exactly 1 in channel
- [ ] 9.3 Create channel backpressure deadlock test: bounded producer-consumer, Coyote explores 500 interleavings, assert no deadlock
- [ ] 9.4 Create pipeline completion test: verify all events delivered and writer properly completed under all interleavings
