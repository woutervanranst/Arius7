## Why

All logging currently goes to stdout via the console provider (Warning+ only) and Spectre.Console (user-facing UI). There is no persistent record of what happened during an operation. When troubleshooting issues — especially tracing a specific file through the archive/restore pipeline — there is nothing to look at after the fact. A file-based audit trail would provide a detailed, greppable log of every file's journey through the pipeline, making it easy to diagnose problems without reproducing them.

## What Changes

- Add **Serilog** with a file sink as the logging provider, writing to `~/.arius/{account}-{container}/logs/{timestamp}_{command}.txt`. One log file per CLI invocation.
- Add **audit-trail-level `LogInformation` calls** throughout the archive, restore, and ls pipeline handlers. Every file should be traceable through every stage (enumerate, hash, dedup, upload/tar, pointer). Log messages use the file's relative path as the natural trace key and include a stage tag (`[hash]`, `[dedup]`, `[upload]`, `[tar]`, etc.).
- **Hash truncation**: All hashes in log messages display only the first 8 hex characters (like git short hashes) to reduce visual noise.
- **Humanized sizes**: File and chunk sizes in log messages use Humanizer (e.g. "4.2 MB" not "4404019"). Add `Humanizer.Core` as a dependency to `Arius.Core`.
- **Thread ID** included in every log line for concurrency debugging.
- **Spectre.Console output capture**: Use `Recorder(AnsiConsole.Console)` to capture all terminal output and append it as a plain-text block at the end of the log file. This includes tables (ls output, rehydration cost estimates).
- Console logging behavior **unchanged** — stays at Warning+ level, no new console output.
- Log file cleanup/rotation is out of scope — left as a future concern.

## Capabilities

### New Capabilities

- `audit-logging`: Defines the file-based audit logging system — log file location, naming, content format, per-file tracing, stage tags, hash truncation, and Spectre.Console output capture.

### Modified Capabilities

_(none — the CLI interface and existing ILogger warning/error behavior are unchanged)_

## Impact

- **Arius.Cli**: Replace `Microsoft.Extensions.Logging.Console` with Serilog configuration (console sink at Warning+, file sink at Information+). Add `Serilog`, `Serilog.Extensions.Logging`, `Serilog.Sinks.File` dependencies. Wrap `AnsiConsole` calls with `Recorder` to capture output. Inject `ILogger` into command handlers for bridging Spectre output to the log file.
- **Arius.Core**: Add `Humanizer.Core` dependency. Add `LogInformation` calls at key pipeline stages in `ArchivePipelineHandler`, `RestorePipelineHandler`, `LsHandler`, and `LocalFileEnumerator`. Existing `LogWarning`/`LogError` calls unchanged.
- **Arius.Core.Tests / Integration / E2E**: No changes expected — tests use `NullLogger` which absorbs the new log calls.
- **Disk**: New `~/.arius/{account}-{container}/logs/` directory created per repo on first operation. Log files are small text files (one per invocation). Depends on the `human-readable-repo-paths` change for the directory naming scheme.
