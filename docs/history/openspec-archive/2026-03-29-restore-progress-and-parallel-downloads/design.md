## Context

The archive pipeline uses a multi-stage channel-connected pipeline with `Parallel.ForEachAsync` (4 workers) for both hashing and uploading. Progress is tracked via `ProgressStream` wrapping file streams, `IProgress<long>` callbacks from `ArchiveOptions`, `TrackedFile`/`TrackedTar` state machines in `ProgressState`, and mediator events connecting Core to CLI.

The restore pipeline processes chunks in a sequential `foreach` loop (`RestorePipelineHandler.cs:308`). It emits coarse-grained events (`FileRestoredEvent`, `FileSkippedEvent`) only after each file completes. There is no byte-level download progress, no visibility during tree traversal, and no parallelism during downloads.

The existing `ProgressStream` (read-mode wrapper reporting cumulative bytes via `IProgress<long>`) and `RenderProgressBar` utility are reusable as-is.

## Goals / Non-Goals

**Goals:**
- Parallel chunk downloads with bounded concurrency (4 workers), matching archive's pattern
- Continuous progress feedback at every phase: resolving, checking, restoring
- Byte-level per-download progress bars for active downloads
- Aggregate progress bar with dual counters (compressed download bytes + original file bytes)
- Active downloads shown instead of completed files (user can infer completed from counters)

**Non-Goals:**
- Configurable parallelism (hard-coded constant, same as archive)
- Download queue depth display (archive shows this, but restore doesn't need channels so there's no queue to poll)
- Streaming parallelism within a single tar extraction (sequential extraction is fine; parallelism is across chunks)
- Retry/resume logic for failed downloads (out of scope, handle separately)

## Decisions

### Decision 1: Use `Parallel.ForEachAsync` directly, not channels

Archive uses a multi-stage channel pipeline because each stage's output feeds the next (enumerate → hash → dedup → route → upload). Restore downloads are embarrassingly parallel: each chunk is independent with no inter-chunk data flow. `Parallel.ForEachAsync` over the chunk list is sufficient.

**Alternative considered**: Channel-based pipeline matching archive exactly. Rejected because it adds complexity (producer task, channel wiring, completion signaling) with no benefit -- there's only one stage.

### Decision 2: `CreateDownloadProgress` callback factory on `RestoreOptions`

Mirror the archive pattern: `RestoreOptions` gets a `CreateDownloadProgress(string identifier, long compressedSize, DownloadKind kind) → IProgress<long>` factory. The pipeline calls it before each download, wraps the download stream with `ProgressStream`, and the CLI uses the callback to create/update `TrackedDownload` entries.

`DownloadKind` is an enum: `LargeFile`, `TarBundle`. This lets the CLI factory set the right display metadata (file name vs "TAR bundle (N files, X)").

For large files, `identifier` is the file's `RelativePath`. For tar bundles, `identifier` is the chunk hash (internal, not displayed -- the CLI uses metadata from the event to build the display label).

**Alternative considered**: Emit events instead of callbacks. Rejected because `IProgress<long>` integrates directly with `ProgressStream` (the existing pattern), and events would add chattiness for high-frequency byte updates.

### Decision 3: Single `TrackedDownload` entity for both large files and tars

Rather than separate `TrackedLargeFile` and `TrackedTar` entities (like archive's `TrackedFile` vs `TrackedTar`), use a single `TrackedDownload` with a `Kind` discriminator. Restore downloads have a simpler lifecycle than archive (just `Downloading → Done`), so a unified type reduces complexity.

Fields:
- `Key` (string) -- unique identifier for the dictionary (RelativePath for large files, chunk hash for tar bundles)
- `Kind` (DownloadKind) -- `LargeFile` or `TarBundle`
- `DisplayName` (string) -- file path for large files, "TAR bundle (N files, X)" for tars
- `CompressedSize` (long) -- total download size
- `BytesDownloaded` (long, Interlocked) -- cumulative bytes for progress bar
- `OriginalSize` (long) -- sum of original file sizes (for aggregate counter)

### Decision 4: Tree traversal progress via file-count event

Emit `TreeTraversalProgressEvent(int FilesFound)` periodically during `WalkTreeAsync` with the cumulative count of files discovered. The CLI handler updates a counter displayed as `○ Resolving  523 files...`. After traversal completes, the existing `TreeTraversalCompleteEvent` fires and the display transitions to `● Resolved ... (1,247 files)`.

To avoid excessive event overhead (one per file), batch the event: emit every N files (e.g., every 10 or every 100ms elapsed). A simple counter with periodic publish keeps I/O overhead negligible.

**Alternative considered**: Count tree blobs downloaded instead of files discovered. Rejected because file count is more meaningful to the user.

### Decision 5: Checking phase as a progressive display

The conflict check loop (`RestorePipelineHandler:126-167`) already emits `FileDispositionEvent` per file. The CLI already handles these and updates `DispositionNew`, `DispositionSkipIdentical`, etc. The display just needs to show `○ Checking` while dispositions are in progress and `● Checked` when chunk resolution begins. No new events needed -- just a display logic change based on existing state transitions.

### Decision 6: Populate sizes after chunk resolution, not tree traversal

`TreeTraversalCompleteEvent` currently sends `TotalOriginalSize: 0` because file sizes aren't known until chunk index lookup. Rather than adding a second tree-traversal pass or changing the tree format, enrich `ChunkResolutionCompleteEvent` to carry `TotalOriginalBytes` and `TotalCompressedBytes`. The display updates the "Resolved" line retroactively when these become available, and uses `TotalCompressedBytes` as the denominator for the aggregate download progress bar.

The `ChunkResolutionCompleteEvent` already fires at exactly the right time (after `_index.LookupAsync` which returns `ShardEntry` with `OriginalSize` and `CompressedSize` per content hash). The pipeline simply sums these before publishing the event.

### Decision 7: Thread-safe counter updates in parallel download loop

Replace `int filesRestored` with a shared `long` updated via `Interlocked.Increment`. The `_mediator.Publish` calls from within parallel workers are already proven thread-safe (archive publishes `ChunkUploadingEvent`/`ChunkUploadedEvent` from 4 concurrent upload workers).

### Decision 8: Display layout for the Restoring phase

```text
  ○ Restoring   312/847 files  ████████░░░░░░░░  38%  (3.17 / 8.31 GB download, 14.2 GB original)

  vacation/photos/sunset.jpg
  ██████████░░░░  72%  18.3 / 25.4 MB
  TAR bundle (3 files, 847 KB)
  ████░░░░░░░░░░  31%   4.8 / 15.2 MB
```

Active downloads are keyed by chunk hash in `ProgressState.TrackedDownloads` (`ConcurrentDictionary<string, TrackedDownload>`). Added when `CreateDownloadProgress` is called, removed when `FileRestoredEvent` (large) or a new `ChunkDownloadCompletedEvent` (tar) fires. The display filters to entries with `BytesDownloaded < CompressedSize`.

Reuse `RenderProgressBar`, `SplitSizePair`, and `TruncateAndLeftJustify` from archive display. Same borderless table layout.

## Risks / Trade-offs

**[Risk] Parallel disk writes could contend on I/O** → Mitigation: 4 workers is conservative. Restore is network-bound (download from Azure), not disk-bound. Same concurrency as archive uploads, which also write to Azure blob storage.

**[Risk] Tar extraction from parallel workers creating overlapping directories** → Mitigation: `Directory.CreateDirectory` is safe for concurrent calls (no-op if exists). Each chunk hash maps to a distinct set of files, so no two workers write the same file path.

**[Risk] `FileDispositionEvent` batching could make Checking phase appear stuck for large repos** → Mitigation: The conflict check involves local file hashing which is I/O-bound. Events fire per file, so the counter should tick visibly. If future profiling shows it's too slow, hashing during conflict check could be parallelized -- but that's a separate concern.

**[Risk] `TrackedDownload` entries not cleaned up if download fails** → Mitigation: Wrap the parallel download body in try/catch that removes the `TrackedDownload` on failure. The exception propagates to `Parallel.ForEachAsync` which cancels remaining workers.

**[Trade-off] Compressed bytes for per-item progress vs original bytes for aggregate** → The per-item bars show compressed download bytes (what's actually moving over the wire). The aggregate line shows both compressed and original. This is slightly asymmetric but accurate -- the per-item bar matches what the user sees in network throughput, while the aggregate gives the "how big are my files" context.
