## 1. Dependencies and Serilog setup

- [x] 1.1 Add `Serilog`, `Serilog.Extensions.Logging`, `Serilog.Sinks.Console`, `Serilog.Sinks.File`, `Serilog.Enrichers.Thread` package references to `Arius.Cli.csproj` (and `Directory.Packages.props`)
- [x] 1.2 Add `Humanizer.Core` package reference to `Arius.Core.csproj`
- [x] 1.3 Replace `services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning))` in `CliBuilder.BuildProductionServices()` with Serilog configuration: console sink at Warning, file sink at Information, thread ID enricher
- [x] 1.4 Compute the log file path (`~/.arius/{account}-{container}/logs/{yyyy-MM-dd_HH-mm-ss}_{command}.txt`) and ensure the `logs/` directory is created before configuring the file sink
- [x] 1.5 Configure the file sink output template: `[{Timestamp:HH:mm:ss.fff}] [{Level:u3}] {Message}{NewLine}`

## 2. Spectre.Console Recorder integration

- [x] 2.1 In each command handler (archive, restore, ls), create a `Recorder` wrapping `AnsiConsole.Console` at handler entry
- [x] 2.2 Replace all `AnsiConsole.*` calls in `CliBuilder.cs` with `recorder.*` calls (34 call sites) — implemented via `AnsiConsole.Console = recorder` swap, no per-callsite changes needed
- [x] 2.3 At the end of each command handler, call `recorder.ExportText()` and log it via `ILogger.LogInformation("--- Console Output ---\n{Output}", text)` — implemented in `FlushAuditLog`
- [x] 2.4 Inject `ILogger<CliBuilder>` (or a named logger) into the command action handlers for the Recorder output flush — not needed; `FlushAuditLog` uses static `Log.Logger`

## 3. Archive pipeline audit trail

- [x] 3.1 Add a hash truncation helper (extension method or local function `hash[..8]`) for use in log messages
- [x] 3.2 Add `[scan]` log: enumeration complete summary (total file count) in `ArchivePipelineHandler`
- [x] 3.3 Add `[hash]` log: per-file hash result (path, truncated hash, humanized size) in the hash worker loop
- [x] 3.4 Add `[dedup]` log: batch summary (batch size, hit count, miss count) and per-file routing decision (hit / new+large / new+small) in the dedup stage
- [x] 3.5 Add `[upload]` log: per-chunk upload start and completion (truncated hash, original size, compressed size humanized) in the large upload workers
- [x] 3.6 Add `[tar]` log: tar seal summary (truncated tar hash, file count, total size), individual file listing, upload result (compressed size), thin chunk creation count
- [x] 3.7 Add `[index]` log: flush summary (new entry count) after `ChunkIndexService.FlushAsync()`
- [x] 3.8 Add `[tree]` log: Merkle tree build summary (truncated root hash, level count) after `FileTreeBuilder.BuildAsync()`
- [x] 3.9 Add `[snapshot]` log: snapshot creation (timestamp) after `SnapshotService.CreateAsync()`
- [x] 3.10 Add operation start marker: command, source directory, target account/container, options
- [x] 3.11 Add operation end marker: summary stats (scanned, uploaded, deduped, transferred, duration)

## 4. Restore pipeline audit trail

- [x] 4.1 Add `[snapshot]` log: resolved snapshot timestamp and truncated root hash
- [x] 4.2 Add `[tree]` log: traversal progress (directories traversed, files collected)
- [x] 4.3 Add `[conflict]` log: per-file conflict resolution (skipped/overwrite/new)
- [x] 4.4 Add `[chunk]` log: chunk resolution summary (large vs tar-bundled, group count)
- [x] 4.5 Add `[rehydration]` log: status check results (available, rehydrated, needs rehydration, pending counts)
- [x] 4.6 Add `[download]` log: per-chunk download progress (truncated hash, compressed size, file count)
- [x] 4.7 Add operation start and end markers for restore

## 5. Ls command audit trail

- [x] 5.1 Add `[snapshot]` log: resolved snapshot
- [x] 5.2 Add tree traversal and match summary log (prefix, filter, files matched)
- [x] 5.3 Add operation start and end markers for ls

## 6. Testing and verification

- [x] 6.1 Run full test suite (`dotnet test`) — existing tests use `NullLogger` so new log calls should be absorbed
- [ ] 6.2 Manual verification: run an archive operation and inspect the generated log file for correct format, stage tags, hash truncation, humanized sizes, thread IDs, and console output capture
