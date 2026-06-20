# Arius.Cli host

> **Code:** `src/Arius.Cli/`  ·  **Decisions:** [ADR-0007](../../decisions/adr-0007-separate-phase-and-detail-logging-in-pipeline-handlers.md) · [ADR-0013](../../decisions/adr-0013-core-host-separation.md)  ·  **Terms:** [chunk](../../glossary.md#chunk) · [large chunk](../../glossary.md#large-chunk) · [tar chunk](../../glossary.md#tar-chunk) · [content hash](../../glossary.md#content-hash) · [snapshot](../../glossary.md#snapshot) · [storage tier hint](../../glossary.md#storage-tier-hint)

## Purpose

The `arius` command-line host. It owns nothing of the backup model: each verb (`archive`, `restore`, `ls`, `repair-index`, `update`) parses flags, builds a **per-repository** `IServiceProvider`, drives Arius.Core through `IMediator`, and turns the events Core publishes during a run into a live terminal display. Maintainer-facing — for user/operator usage see [guide/cli.md](../../guide/cli.md).

## How it works

### One provider per repository (no shared host container)

There is no long-lived application container. `Program.cs` builds the `RootCommand` once (`CliBuilder.BuildRootCommand`) and each verb lazily constructs a `ServiceProvider` scoped to exactly one account + container, via the `serviceProviderFactory` delegate injected into every verb's `Build`. `CliBuilder.BuildProductionServices` opens the blob container (`IBlobServiceFactory.CreateAsync` → `OpenContainerServiceAsync(container, preflightMode)`) and then wires DI:

- `ProgressState` as a **singleton** — the one piece of shared mutable state, read by the display and written by the notification handlers.
- `AddMediator()` is called **in the CLI assembly, not inside `AddArius`**, on purpose: the `Mediator.SourceGenerator` must run in `Arius.Cli` so it discovers `INotificationHandler<T>` implementations in *both* Core and the CLI (the progress handlers in `Commands/Archive/ArchiveProgressHandlers.cs` etc. live here).
- `AddArius(blobContainer, passphrase, accountName, containerName)` registers the Core feature handlers.

`PreflightMode` (`ReadWrite` for archive/repair, `ReadOnly` for restore/ls) is threaded into the factory so the container open fails fast; verbs catch `PreflightException` and translate its structured fields (`ErrorKind`, `AuthMode`, account/container names) into actionable console messages — the long `switch` in each verb is the same shape across `ArchiveVerb`, `RestoreVerb`, `LsVerb`.

### Driving Core and consuming its events

The CLI sends a single command per verb and renders progress purely as a side effect of Core's published events — it never inspects Core internals. The Core → CLI direction is loosely coupled through Mediator notifications; **that emission and loose-coupling design lives in [cross-cutting/events-and-progress.md](../cross-cutting/events-and-progress.md) — this doc only covers the consume/render side.**

```mermaid
sequenceDiagram
    participant V as ArchiveVerb.SetAction
    participant M as IMediator
    participant Core as ArchiveCommandHandler
    participant H as INotificationHandler&lt;T&gt; (CLI)
    participant PS as ProgressState (singleton)
    participant Live as AnsiConsole.Live loop

    V->>M: Send(ArchiveCommand(opts))  [archiveTask, not awaited]
    par Pipeline runs
        Core-->>H: Publish FileScannedEvent / FileHashedEvent / ChunkUploadedEvent …
        H->>PS: IncrementFilesScanned / SetFileHashed / IncrementChunksUploaded …
    and Poll loop (~10 Hz)
        loop until archiveTask.IsCompleted
            Live->>PS: read counters + tracked rows
            Live->>Live: ctx.UpdateTarget(BuildDisplay(state))
            Live->>Live: await Task.WhenAny(archiveTask, Task.Delay(100))
        end
    end
    M-->>V: ArchiveResult
```

Two distinct event channels feed `ProgressState`:

1. **Notifications** — handlers in `ArchiveProgressHandlers.cs` / `Restore/RestoreProgressHandlers.cs` mutate `ProgressState`. They are pure state transitions (e.g. `FileHashedHandler` → `state.SetFileHashed`, `ChunkUploadedHandler` → remove the tracked row + `IncrementChunksUploaded`).
2. **Per-item `IProgress<long>` callbacks** — for byte-level progress on a *specific* file/chunk. The verb supplies factory delegates on the Core options (`CreateHashProgress`, `CreateUploadProgress`, `CreateLargeFileDownloadProgress`, `CreateTarBundleDownloadProgress`) that hand back a `Progress<long>` writing into the matching `TrackedFile` / `TrackedTar` / `TrackedDownload`. Queue depths arrive the same way via `OnHashQueueReady` / `OnUploadQueueReady` / `OnDownloadQueueReady`, which stash a `Func<int>` the display polls.

### ProgressState: the shared model

`ProgressState` (singleton) holds all live counters plus per-item tracking dictionaries. Every field uses `Interlocked` / `Volatile` or a `Concurrent*` collection because Core's pipeline stages publish from many worker threads concurrently while the display reads on the poll thread. Three per-item entities track the rows the display shows:

- **`TrackedFile`** — a file moving through the archive `FileState` machine `Hashing → Hashed → Uploading → Done`. Only `Hashing` and `Uploading` are visible as rows; `Hashed` is invisible but retained so the `ContentHashToPath` reverse map can find it. The reverse map ([content hash](../../glossary.md#content-hash) → paths) is the bridge that lets content-hash-keyed events (`ChunkUploadingEvent`, `ChunkUploadedEvent`) and the chunk-keyed upload-progress callback find the per-file rows — large-file [chunk hashes](../../glossary.md#chunk-hash) equal the file's content hash, so `ContentHash.Parse(chunkHash)` reconnects them.
- **`TrackedTar`** — one [tar chunk](../../glossary.md#tar-chunk) bundle through `Accumulating → Sealing → Uploading`. Small files are *collapsed* into a TAR row (`TarEntryAddedHandler` removes the individual `TrackedFile` and adds to the bundle), so a single bundle row stands in for many small files.
- **`TrackedDownload`** — one restore chunk download (large-file or tar-bundle), keyed by relative path or chunk hash.

### Rendering: `BuildDisplay` as a pure function

Each verb has an `internal static IRenderable BuildDisplay(ProgressState)` that is a **pure projection of the current state** — no rendering decision reads anything but `ProgressState`. The poll loop calls it repeatedly:

```csharp
await AnsiConsole.Live(BuildDisplay(progressState))
    .Overflow(VerticalOverflow.Crop).Cropping(VerticalOverflowCropping.Bottom)
    .AutoClear(false)
    .StartAsync(async ctx =>
    {
        var archiveTask = mediator.Send(new ArchiveCommand(opts), ct).AsTask();
        while (!archiveTask.IsCompleted)
        {
            ctx.UpdateTarget(BuildDisplay(progressState));
            await Task.WhenAny(archiveTask, Task.Delay(100, ct));   // ~10 Hz, responsive cancel
        }
        result = await archiveTask;
        ctx.UpdateTarget(BuildDisplay(progressState));               // final frame
    });
```

The layout is built bottom-up from `ProgressState`: phase headers (Scanning / Hashing / Uploading for archive; Resolved / Checked / Restoring for restore) each render a `●`/`○` symbol whose state is *derived from the counters* (e.g. archive's Hashing header is "done" only when `ScanComplete && filesHashed + filesSkipped >= filesScanned`), followed by a borderless `Table` of the active per-item rows. `DisplayHelpers` provides the shared cell formatting: `RenderProgressBar` (green `█` / dim `░`), `SplitSizePair` (aligns a current/total byte pair onto a shared unit), and `TruncateAndLeftJustify` (fixed-width path cell that keeps the *deepest* path segment via a `"..." + tail` ellipsis).

When stdout is not interactive (`!Console.Profile.Capabilities.Interactive`, e.g. piped/CI), the Live loop is skipped entirely and the command is awaited directly — the events still fire and update `ProgressState`, they just aren't rendered.

### Restore: interactive phase coordination

Restore is the one verb that must *ask the user a question mid-pipeline* (rehydration cost confirmation, cleanup confirmation) — but the prompt and the pipeline run on different threads. `RestoreVerb` bridges them with `TaskCompletionSource` pairs: Core's `ConfirmRehydration` / `ConfirmCleanup` option delegates set a "question" TCS and `await` an "answer" TCS; the CLI's outer loop watches the question TCSs alongside the pipeline task (`Task.WhenAny(pipelineTask, questionTcs.Task, cleanupQuestionTcs.Task, Task.Delay(100))`), tears down the Live display when a question fires, renders the cost table / `SelectionPrompt`, and posts the answer back through the answer TCS. The numbered `// ── Phase N ──` comments in `RestoreVerb` mark this: resolve → rehydration question → download (a second Live loop) → cleanup question.

### `ls`, `repair-index`, `update`

- **`ls`** does *not* use a Live display or a recorder. It streams entries via `mediator.CreateStream(new ListQuery(...))` and writes each row as it arrives (`AnsiConsole.MarkupLine` per `RepositoryFileEntry`), so memory stays bounded for million-entry repositories. The 4-char state cell (`PBRH`-style: local Pointer / local Binary / in Repository / tier H·A·~·?) is formatted by `LsStateFormatter`, whose colors mirror Arius.Explorer.
- **`repair-index`** is a fire-and-forget command (`RepairChunkIndexCommand`) with a recorder-captured single summary line.
- **`update`** is self-contained — no Core, no DI. It queries the GitHub releases API, maps the `RuntimeIdentifier` to a release asset, downloads it under a `Progress` bar, and replaces the running executable (on Windows via the embedded `WindowsUpdateAfterExit.ps1` helper that waits for the process to exit; on Unix via copy-overwrite + `chmod`).

### Audit logging

Per ADR-0007 the pipeline emits two log levels (coarse `[phase]` markers + category detail). The CLI is where they land: `CliBuilder.ConfigureAuditLogging` configures a per-invocation Serilog file sink under `RepositoryLocalStatePaths.GetLogsDirectory(account, container)` at `Information`+, with a `{ShortSourceContext}` template. Verbs that own a Live display swap `AnsiConsole.Console` for a `Recorder`, then `FlushAuditLog` writes the captured console text into the log on exit — so the audit log preserves both the structured pipeline events and what the user actually saw.

## Key invariants

- **`ProgressState` is the only shared mutable state, and every access is thread-safe.** Core publishes from concurrent worker threads while the display reads on the poll thread; all counters use `Interlocked`/`Volatile` and all collections are `Concurrent*`. A handler must not introduce a non-atomic field.
- **`BuildDisplay` is a pure function of `ProgressState`.** No I/O, no Core calls, no side effects — it can be invoked at any frame rate and at completion. Display logic that needs new information must first land in `ProgressState`.
- **The reverse map `ContentHashToPath` must be populated (on `FileHashedEvent`) before any content-hash-keyed event for that hash arrives.** Large-file rows depend on `ContentHash.Parse(chunkHash)` reconnecting to it; the pipeline's ordering guarantee is load-bearing.
- **Small files collapse into exactly one `TrackedTar` row.** `TarEntryAddedHandler` removes the per-file `TrackedFile` when the file joins a bundle; nothing should re-add it. Upload progress for a bundle is measured against the *sealed* `TarByteSize` (set in `TarBundleSealingHandler`), not the sum of file contents, or the bar overshoots 100%.
- **One `IServiceProvider` per repository.** Account/container/passphrase are baked into the provider at construction; there is no cross-repository state to leak between verbs.
- **Non-interactive runs still complete correctly.** Skipping the Live loop must never change command behavior — only whether frames are drawn.

## Why this shape

- **Core ⊥ hosts ([ADR-0013](../../decisions/adr-0013-core-host-separation.md)).** The CLI consumes Core only through `IMediator` + option delegates, never reaching into pipeline internals; the same Core is reused unchanged by Explorer and the Web/Api host. This is why progress flows through published events into a host-owned `ProgressState` rather than Core knowing about a terminal.
- **Two-level audit logging ([ADR-0007](../../decisions/adr-0007-separate-phase-and-detail-logging-in-pipeline-handlers.md)).** The CLI is the sink for the phase/detail taxonomy; see the ADR for why phases are entry-markers (not begin/end spans) given overlapping concurrent stages.
- **Mediator registered in the CLI assembly.** The source generator must scan `Arius.Cli` to find the progress handlers; registering inside `AddArius` would only discover Core handlers. This is the one piece of DI wiring that can't move into Core.
- **`Task.WhenAny(work, Task.Delay(100))` instead of a timer.** A ~10 Hz poll keeps the display responsive and, critically, keeps the loop awaiting the real work task so cancellation propagates immediately rather than on the next tick.
- **TCS pairs for restore prompts.** A prompt that blocks a pipeline thread would stall Core's workers; routing question/answer through `TaskCompletionSource` lets the pipeline keep running (or park) while the CLI thread owns all console interaction.

## Open seams / future

- **Display logic is duplicated per verb.** `ArchiveVerb.BuildDisplay` and `RestoreVerb.BuildDisplay` each re-implement the phase-header + table pattern; only the leaf cell helpers (`DisplayHelpers`) are shared. A common "phase header + tracked-row table" renderable would remove the divergence — the next display addition is the place to factor it out.
- **The `PreflightException` → message `switch` is copy-pasted** across `ArchiveVerb`, `RestoreVerb`, `LsVerb` (differing only in the RBAC role suggested). A shared formatter keyed on `PreflightMode` would collapse it.
- **`repair-index` has no progress display** — it runs blind to a single summary line even though `RepairChunkIndexCommand` could publish events. If repair grows long-running, it should adopt the same `ProgressState`/Live pattern.
- **`update` trusts the GitHub asset unconditionally** (no checksum/signature verification) and only supports the four RIDs in `ridToAsset`.
