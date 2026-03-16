## Context

Arius is a deduplicating backup tool that stores encrypted, content-addressed packs in Azure Blob Storage. Both backup and restore operations are fully sequential today:

- **Backup** (`BackupHandler.cs`): iterates files one-by-one → chunks with GearChunker → hashes with HMAC-SHA256 → checks dedup → accumulates in PackerManager (10MB threshold) → seals (TAR→gzip→AES-256-CBC) → uploads to Azure → writes snapshot + index. Every step `await`s before the next.
- **Restore** (`RestoreHandler.cs`): loads snapshot + index → for each file, for each chunk → on cache miss downloads pack → decrypts + extracts → verifies HMAC → writes to output. All packs stay in a `Dictionary<string, Dictionary<string, byte[]>>` forever → OOM on large restores.

The `IStreamRequestHandler<TRequest, TEvent>` interface returns `IAsyncEnumerable<TEvent>` — events flow to CLI/Web consumers for progress display.

All infrastructure is async (`IBlobStorageProvider`, `CryptoService`, `PackerManager`, `GearChunker`) and stateless/static where possible, making parallelization feasible without major architectural changes.

**Constraints:**
- .NET 10.0, TUnit test framework, Spectre.Console CLI
- GearChunker uses rolling hash state → per-file chunking must remain sequential
- `IAsyncEnumerable<TEvent>` must be produced from a single `async` method (C# constraint)
- Azure Blob Storage handles concurrent requests well but client machine resources vary

## Goals / Non-Goals

**Goals:**
- 2–5× backup throughput by overlapping disk I/O, CPU (hash/compress/encrypt), and network (upload)
- 2–5× restore throughput by parallelizing pack downloads and file assembly
- Bounded memory: restore memory O(concurrent_workers) instead of O(total_packs)
- Correct dedup under concurrency: each unique blob packed exactly once
- Collect-and-report error handling: individual file failures don't cancel the operation
- Chunk-level dedup statistics in completion events
- Human-readable CLI output via Humanizer
- Comprehensive concurrency test coverage

**Non-Goals:**
- Streaming pipeline for individual large files (chunking stays per-file sequential)
- Distributed/multi-machine parallelism
- Changing the pack format, encryption scheme, or repository layout
- Parallelizing GearChunker itself (rolling hash is inherently sequential)
- Real-time rehydration overlapping (Archive tier packs require separate rehydration phase — out of scope, handled by existing restore spec)

## Decisions

### D1: Channel-based producer-consumer for backup pipeline

**Choice:** `System.Threading.Channels` with bounded channels and backpressure.

**Alternatives considered:**
- `Parallel.ForEachAsync` — simpler but doesn't support multi-stage pipelines with different concurrency per stage
- `System.Threading.Tasks.Dataflow` (TPL Dataflow) — heavier dependency, similar capabilities but less idiomatic in modern .NET
- Raw `Task.WhenAll` with `SemaphoreSlim` — works for flat parallelism but messy for staged pipelines

**Rationale:** Channels are built into .NET, lightweight, support bounded capacity for backpressure, and naturally model producer-consumer relationships. Each pipeline stage runs at its own concurrency level with backpressure propagating upstream when downstream is slow.

**Pipeline topology:**
```
[File Processors]  →  Channel<BlobToPack>  →  [Accumulator]  →  Channel<List<BlobToPack>>  →  [Seal Workers]  →  Channel<SealedPack>  →  [Uploaders]
    (N workers)         (bounded)              (1 consumer)       (bounded)                    (M workers)         (bounded)              (P workers)
```

### D2: Atomic dedup via ConcurrentDictionary.TryAdd

**Choice:** Replace `HashSet<string> seenThisRun` with `ConcurrentDictionary<string, byte>` using `TryAdd()` as an atomic claim operation.

**Rationale:** `TryAdd` returns `true` only for the first caller with a given key — this is the exact semantics needed for "claim this blob for packing". No lock contention, no check-then-act race. `existingIndex` is read-only after load, safe for concurrent `ContainsKey` calls.

**Failed upload recovery:** If a pack upload fails, its index entries are not written. On re-run, those blobs won't be in `existingIndex`, so they'll be re-claimed and re-uploaded. Self-healing by design.

### D3: Disk-based temp staging for restore

**Choice:** Extract blobs to `{tempDir}/{blobHash}.bin` files during pack download, then read from disk during file assembly. Clean up temp dir after completion.

**Alternatives considered:**
- In-memory cache with LRU eviction — complex, still risks OOM with large blobs
- Memory-mapped files — platform-dependent, complex lifecycle management
- Stream individual blobs without caching — too many redundant pack downloads (multiple files reference same pack)

**Rationale:** Disk is cheap and bounded. Temp usage ≈ total restore size (user needs that space anyway for output). Memory drops from O(total_packs × 10MB) to O(concurrent_workers × 29MB). Each worker holds at most one pack in memory during extraction, then releases it.

### D4: Event channel for IAsyncEnumerable bridging

**Choice:** Internal `Channel<TEvent>` that all pipeline workers write to. The `Handle` method reads from the channel and `yield return`s to the consumer.

```csharp
public async IAsyncEnumerable<BackupEvent> Handle(BackupRequest request, ...)
{
    var events = Channel.CreateUnbounded<BackupEvent>();
    var pipeline = Task.Run(() => RunPipelineAsync(request, events.Writer, ct));
    await foreach (var evt in events.Reader.ReadAllAsync(ct))
        yield return evt;
    await pipeline;
}
```

**Rationale:** Preserves the `IAsyncEnumerable` contract. Events arrive in whatever order workers produce them — ordering is the consumer's responsibility (as established in our exploration). `Channel.CreateUnbounded` is fine here because events are tiny (just metadata).

### D5: Concurrency defaults

**Choice:** `Environment.ProcessorCount` for CPU-bound stages, fixed `4` for network-bound stages.

| Stage | Default | Rationale |
|-------|---------|-----------|
| File processors | `ProcessorCount` | CPU-bound (hash + chunk) + disk I/O |
| Pack accumulator | `1` | Sequential by design (fill bucket, seal) |
| Seal workers | `max(1, ProcessorCount / 2)` | CPU-heavy (gzip + AES), ~29MB RAM each |
| Uploaders | `4` | Network-bound, Azure handles concurrent uploads well |
| Downloaders (restore) | `4` | Network-bound |
| Assemblers (restore) | `ProcessorCount` | Disk I/O + HMAC verify |

All configurable via `ParallelismOptions`.

### D6: Humanizer in CLI only

**Choice:** Add `Humanizer.Core` to `Arius.Cli` only. Core produces raw numbers, CLI humanizes them.

**Rationale:** Keeps `Arius.Core` dependency-free. CLI already owns display formatting (Spectre.Console). Humanizer replaces manual `FormatBytes` helpers and adds relative timestamps, quantities, and durations.

### D7: Three-layer concurrency testing

**Choice:**
1. **Layer 1** — Deterministic tests (TUnit, `Barrier`-based): force specific interleavings, assert exact outcomes
2. **Layer 2** — Stress tests (TUnit, Azurite): 1000+ files, high parallelism, byte-level verification
3. **Layer 3** — Systematic exploration (Coyote, separate project targeting net8.0): explores all interleavings

**Rationale:** Layer 1 catches known races cheaply. Layer 2 catches emergent issues under load. Layer 3 provides exhaustive coverage but needs a separate target framework due to Coyote's .NET 10 compatibility uncertainty.

## Risks / Trade-offs

- **[Memory pressure with high parallelism]** → Mitigated by bounded channels with backpressure and configurable concurrency limits. Seal workers are the dominant consumer (~29MB each) — default M=ProcessorCount/2 keeps this manageable.
- **[Temp disk space during restore]** → Temp dir ≈ total restore size. Mitigated by documenting the requirement and allowing configurable `TempPath`. Cleanup in `finally` block.
- **[Coyote incompatible with .NET 10]** → Coyote test project multi-targets `net8.0`. If Coyote breaks entirely, Layers 1+2 provide sufficient coverage.
- **[Event ordering changes]** → Breaking for consumers that assume ordered events. Mitigated by documenting in proposal. CLI/Web must sort if needed.
- **[Azure rate limiting under high concurrency]** → Azure SDK has built-in retry with exponential backoff. Default concurrency (4) is conservative. Configurable for users with premium storage.
- **[Interrupted restore leaves temp dir]** → Cleanup in `finally` block. Could also add startup cleanup of stale temp dirs with age check.

## Open Questions

None — all design decisions were resolved during the exploration phase.
