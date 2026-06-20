## Context

The archive display uses Spectre.Console `Live` with a pure-function renderer (`BuildArchiveDisplay`) polled every 100ms. The pipeline publishes Mediator events; CLI handlers mutate a shared `ProgressState` singleton; the display reads `ProgressState` and builds a `Rows(...)` renderable.

The current design tracks every file individually through `TrackedFiles` from hashing to upload completion. This creates visual "stuck" states: files at `Hashing 100%` waiting for dedup/routing, and files in `Queued in TAR` with no progress indication. The display conflates "bytes read from disk" with "hashing" and shows no activity during inter-stage channel waits.

Pipeline topology for reference:
```
Enumerate (×1) → Hash (×4) → Dedup (×1) → Router → Large Upload (×4)
              channel(64)   channel(64)           → Tar Builder (×1) → Tar Upload (×4)
                                                    channel(64)        channel(32)
```

## Goals / Non-Goals

**Goals:**
- Eliminate "stuck" visual states by showing only files with active byte-level progress
- Make TAR bundles a first-class display entity with accumulation and upload progress
- Provide live scanning counter (per-file events instead of batch)
- Surface pipeline health via queue depth indicators
- Show dedup effectiveness via unique file counter on hashing header
- Add byte-level upload progress for TAR bundles (currently missing)

**Non-Goals:**
- Changing the pipeline architecture, channel capacities, or worker counts
- Modifying the restore display (separate change)
- Adding ETA or throughput calculations
- Web UI support (though the ProgressState model doesn't preclude it)

## Decisions

### 1. Per-file scanning events replace batch event

**Decision**: Replace `FileScannedEvent(long TotalFiles)` with `FileScannedEvent(string RelativePath, long FileSize)` published per file during enumeration, plus a new `ScanCompleteEvent(long TotalFiles, long TotalBytes)` published once when enumeration finishes.

**Rationale**: The current batch event causes the scanning counter to jump from "..." to "1523" at the end of enumeration. Per-file events let the counter tick up live, giving immediate feedback. `ScanCompleteEvent` provides the final total and flips the `●` indicator. The `RelativePath` and `FileSize` are available from `FilePair` at the enumeration site.

**Alternative considered**: Publishing `FileScannedEvent(count)` periodically (e.g., every 100 files). Rejected because it's an arbitrary batching that still produces jumps and adds complexity.

### 2. Simplified FileState enum with invisible Hashed state

**Decision**: Change `FileState` to: `Hashing`, `Hashed`, `Uploading`, `Done`. Remove `QueuedInTar` and `UploadingTar`. The display filters to only show `TrackedFile` entries where `State is Hashing or Uploading`. Files in `Hashed` state remain in `TrackedFiles` (for `ContentHashToPath` lookup) but are invisible.

State transitions:
```
FileHashingEvent    → add TrackedFile, State = Hashing       (VISIBLE)
FileHashedEvent     → State = Hashed                         (INVISIBLE)
TarEntryAddedEvent  → remove from TrackedFiles               (small file, done)
ChunkUploadingEvent → State = Uploading, reset BytesProcessed (VISIBLE, large file)
ChunkUploadedEvent  → remove from TrackedFiles               (large file, done)
```

**Rationale**: The `Hashed` state solves the "re-add" problem for large files. Without it, we'd need to remove the `TrackedFile` at `FileHashedEvent` and re-create it at `ChunkUploadingEvent`, but at that point we only have the content hash, not the relative path or file size. Keeping the entry with an invisible state preserves the lookup chain.

Small files are removed from `TrackedFiles` at `TarEntryAddedEvent` (when they enter the TAR). Their content hash is still in `ContentHashToPath` for any downstream lookups, but the `TrackedFile` entry is gone.

**Alternative considered**: Removing files at `FileHashedEvent` and storing path/size in `ContentHashToPath` for re-creation. Rejected because it duplicates data and adds complexity for something the existing `TrackedFile` already handles.

### 3. TrackedTar as a CLI-side display entity

**Decision**: Introduce `TrackedTar` in `Arius.Cli` (alongside `TrackedFile` in `ProgressState.cs`) with its own state machine:

```
TarState enum: Accumulating, Sealing, Uploading

TrackedTar:
  BundleNumber      : int           (sequential, display-only, assigned by CLI)
  State             : TarState
  FileCount         : int           (updated per TarEntryAddedEvent)
  AccumulatedBytes  : long          (updated per TarEntryAddedEvent)
  TargetSize        : long          (TarTargetSize, for accumulation progress bar)
  TotalBytes        : long          (final uncompressed size, set at sealing)
  BytesUploaded     : long          (Interlocked, for upload progress bar)
  TarHash           : string?       (set at sealing, used for upload progress lookup)
```

Lifecycle:
```
TarBundleStartedEvent     → new TrackedTar, State = Accumulating
TarEntryAddedEvent        → update FileCount + AccumulatedBytes on current tar
TarBundleSealingEvent     → State = Sealing, set TarHash + TotalBytes
ChunkUploadingEvent       → if TarHash matches: State = Uploading
TarBundleUploadedEvent    → remove TrackedTar
```

Progress bars:
- **Accumulating**: `AccumulatedBytes / TargetSize` (shows how full the TAR is before sealing)
- **Sealing**: bar frozen at last accumulation value
- **Uploading**: `BytesUploaded / TotalBytes`

**Rationale**: TAR bundles are the natural unit of work for small files. Showing individual small files post-hashing adds noise without information. A TAR line with growing file count and accumulation bar shows the pipeline is active. Bundle numbering (`TAR #1`, `TAR #2`) is purely a CLI concern -- Core just says "new tar started."

**Alternative considered**: Keeping individual small file lines with a "Bundling" state. Rejected because it clutters the display with many static lines (small files don't have byte-level TAR progress) and the TAR-as-entity view is more informative.

### 4. TarBundleStartedEvent with no parameters

**Decision**: Add `TarBundleStartedEvent()` as a parameterless Mediator notification. Published by the TAR builder when it initializes a new tar (before writing the first entry).

**Rationale**: The event marks a lifecycle boundary ("new TAR started") without leaking CLI concerns into Core. Bundle numbering, display state initialization, and `TargetSize` assignment all happen in the CLI handler. Core has no knowledge of display state.

**Alternative considered**: `TarBundleStartedEvent(int BundleNumber)` with Core tracking the counter. Rejected because bundle numbering is a UI concern that should not leak into the pipeline.

### 5. TAR upload progress via existing CreateUploadProgress

**Decision**: Wrap the TAR upload's `FileStream` in `ProgressStream` using the existing `CreateUploadProgress(tarHash, uncompressedSize)` callback. The CLI's callback implementation checks both `TrackedFiles` (for large file hashes) and `TrackedTars` (for tar hashes) to find the right progress target.

Pipeline change (in TAR upload stage):
```csharp
// Current:
await using (var fs = File.OpenRead(sealed_.TarFilePath))
    await fs.CopyToAsync(gzipStream, ct);

// New:
await using (var fs = File.OpenRead(sealed_.TarFilePath))
{
    IProgress<long> progress = opts.CreateUploadProgress?.Invoke(sealed_.TarHash, sealed_.UncompressedSize)
        ?? new Progress<long>();
    await using var ps = new ProgressStream(fs, progress);
    await ps.CopyToAsync(gzipStream, ct);
}
```

CLI callback update:
```csharp
CreateUploadProgress = (contentHash, size) =>
{
    // Try TrackedFile first (large file upload)
    if (progressState.ContentHashToPath.TryGetValue(contentHash, out var paths))
    {
        // ... existing logic ...
    }

    // Try TrackedTar (tar bundle upload)
    var tar = progressState.TrackedTars.Values.FirstOrDefault(t => t.TarHash == contentHash);
    if (tar != null)
    {
        return new Progress<long>(bytes => tar.SetBytesUploaded(bytes));
        }

    return new Progress<long>();
},
```

**Rationale**: Reusing the existing callback avoids adding new properties to `ArchiveOptions`. TAR hashes and content hashes are hashes of different content, so collisions are impossible. The CLI already has the lookup infrastructure.

**Alternative considered**: A separate `CreateTarUploadProgress` factory. Rejected as unnecessary -- the existing factory handles both cases cleanly with a dual lookup.

### 6. Queue depth via callback delegates

**Decision**: Add two optional callback properties to `ArchiveOptions`:
```csharp
public Action<Func<int>>? OnHashQueueReady { get; init; }
public Action<Func<int>>? OnUploadQueueReady { get; init; }
```

The pipeline registers channel readers early in `Handle`:
```csharp
opts.OnHashQueueReady?.Invoke(() => filePairChannel.Reader.Count);
opts.OnUploadQueueReady?.Invoke(() => largeChannel.Reader.Count + sealedTarChannel.Reader.Count);
```

The CLI stores the getters in `ProgressState`:
```csharp
OnHashQueueReady = getter => progressState.HashQueueDepth = getter,
OnUploadQueueReady = getter => progressState.UploadQueueDepth = getter,
```

Display renders `[N pending]` dimmed on the stage header line, only when N > 0.

**Rationale**: The channels are private to `ArchivePipelineHandler` and don't exist until `Handle` runs. A callback-based inversion lets the pipeline say "here's how to read my queue" without exposing channel internals. The CLI stores a `Func<int>` getter and polls it during display updates (every 100ms), which is cheap (`Channel.Reader.Count` is O(1) on bounded channels).

**Alternative considered**: Adding `Func<int>?` properties directly to `ArchiveOptions` for the pipeline to populate. Rejected because the options are created before the pipeline runs, so the channels don't exist yet. The callback pattern solves the timing issue.

### 7. Dedup counter via FilesUnique on ProgressState

**Decision**: Add `FilesUnique` counter to `ProgressState`, incremented when the dedup stage routes a file to upload (not deduped). Displayed on the hashing header as `(N unique)`.

This requires the dedup stage to publish an event or the existing routing logic to signal the CLI. The simplest approach: increment `FilesUnique` in the `ChunkUploadingHandler` (for large files) and in the `TarEntryAddedHandler` (for small files), since both events fire only for files that passed dedup.

Header display: `○ Hashing    720 / 1.523 files (312 unique)    [12 pending]`

**Rationale**: Showing `(N unique)` tells the user how much actual upload work the run will generate. A run where 1400 of 1523 files are deduped produces very little upload traffic -- knowing this sets expectations. No new Core event needed; the CLI can infer uniqueness from existing events.

### 8. Display rendering changes

**Decision**: `BuildArchiveDisplay` renders three sections:

**Stage headers:**
```
  ● Scanning   1.523 files
  ○ Hashing    720 / 1.523 files (312 unique)          [12 pending]
  ○ Uploading  3 unique chunks                         [2 pending]
```

- Scanning: `○` during enum, `●` when `ScanComplete`. Shows `FilesScanned` ticking up.
- Hashing: `FilesHashed / FilesScanned` with `(FilesUnique unique)`. Queue depth from `HashQueueDepth`.
- Uploading: `ChunksUploaded unique chunks`. Queue depth from `UploadQueueDepth`. Only shown when there is upload activity.

**Per-file lines** (only `TrackedFile` entries where `State is Hashing or Uploading`):
```
  ...rview-v2 - WouterNotes.pptx  ██████░░░░░░  Hashing    50%  6,67 / 13,34 MB
  ...FY14 - EMS Plan.pptx         ████████████  Uploading 100%  6,39 / 6,39 MB
```

**TAR lines** (all `TrackedTar` entries):
```
  TAR #1 (23 files, 5,1 MB)       ███░░░░░░░░░  Accumulating    5,1 / 64 MB
  TAR #2 (64 files, 47,8 MB)      ████████████  Sealing        47,8 / 64 MB
  TAR #3 (64 files, 52,1 MB)      ██████████░░  Uploading  83%  43,2 / 52,1 MB
```

**Rationale**: The per-file area only contains items with active byte-level progress or meaningful state changes (TAR accumulation). No items appear "stuck" because the `Hashed` state is invisible and small files move directly into the TAR line.

### 9. ChunkUploadingHandler dual lookup for files and TARs

**Decision**: `ChunkUploadingHandler` tries `SetFileUploading(hash)` first (large files), then `SetTarUploading(hash)` (TAR bundles). Only one lookup will match for any given content hash.

Similarly, `ChunkUploadedHandler` handles large file removal as before. `TarBundleUploadedHandler` handles TAR removal separately. No overlap.

**Rationale**: `ChunkUploadingEvent` is published for both large file uploads and TAR bundle uploads. The handler needs to route to the correct tracking entity. Since tar hashes and content hashes are hashes of different content, collisions are not possible.

## Risks / Trade-offs

- **Per-file scanning events add mediator overhead** → Each of 1500+ files publishes a notification during enumeration. → Mitigation: `FileScannedHandler` does only two `Interlocked` operations (increment counter, add bytes). The mediator publish path is fast (source-generated, no reflection). Channel backpressure is the real bottleneck, not event publishing.

- **Last TAR's accumulation bar never fills** → The final TAR seals when the small channel closes, typically well below `TarTargetSize`. The bar might show 3% then jump to Sealing/Uploading. → Accepted: This accurately shows "the TAR is mostly empty, we're flushing the remainder." No special-casing needed.

- **Queue depth is an approximation** → `Channel.Reader.Count` is a snapshot that may be stale by the time the display renders (100ms poll). → Accepted: It's directionally useful ("is the pipeline backed up?"), not a precise metric. Staleness of 100ms is irrelevant for human perception.

- **TrackedFile entries for small files linger in Hashed state** → Between `FileHashedEvent` and `TarEntryAddedEvent`, small files are invisible but present in `TrackedFiles`. If the pipeline stalls, entries accumulate. → Mitigation: `TarEntryAddedEvent` removes them promptly in normal operation. The `ConcurrentDictionary` handles thousands of entries efficiently. If needed, a periodic sweep could clean stale `Hashed` entries, but this is unlikely to be necessary.

- **CreateUploadProgress dual lookup adds per-TAR-upload cost** → `FirstOrDefault` over `TrackedTars.Values` is O(N) where N is concurrent TARs. → Mitigation: N is tiny (at most `UploadWorkers` = 4 TARs uploading concurrently, plus 1 accumulating). O(4) is effectively O(1).
