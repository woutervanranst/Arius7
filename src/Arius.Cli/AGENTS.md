# Arius.Cli — agent notes

Cross-cutting rules (think-before-coding, simplicity, testing workflow, code style, domain vocabulary, the docs map) live in the root [`../../AGENTS.md`](../../AGENTS.md) — not repeated here. This file is only what's true of **this** project.

- **What it is / how it works:** the Spectre.Console CLI host. Internals: [`../../docs/design/hosts/cli.md`](../../docs/design/hosts/cli.md). The event/progress contract it consumes: [`../../docs/design/cross-cutting/events-and-progress.md`](../../docs/design/cross-cutting/events-and-progress.md). End-user docs: [`../../docs/guide/cli.md`](../../docs/guide/cli.md). Glossary: [`../../docs/glossary.md`](../../docs/glossary.md).

## Layout
- `Commands/{Archive,Ls,Repair,Restore,Update}/` — **one verb per folder** (`archive`, `restore`, `ls`, `repair-index`, `update`). Each is a `static Build(...)` that returns a `System.CommandLine` `Command`.
- `Program.cs` — composition root: `CliBuilder.BuildRootCommand(...).Parse(args).InvokeAsync()`, top-level try/finally for unhandled exceptions and `Log.CloseAndFlush()`.
- `CliBuilder.cs` — root command + shared options (`AccountOption`/`KeyOption`/`PassphraseOption`/`ContainerOption`), credential resolution (`ResolveAccount`/`ResolveKey`: CLI flag → env var → user secrets), per-repo DI (`BuildProductionServices`), and Serilog audit logging.
- `ProgressState.cs` — the singleton progress model (`ProgressState`, `TrackedFile`/`TrackedTar`/`TrackedDownload`).
- `ArchiveProgressHandlers.cs`, `RestoreProgressHandlers.cs` — the `INotificationHandler<T>` set. `DisplayHelpers.cs`, `LsStateFormatter.cs` — rendering helpers.

## Conventions (this project)
- **Verbs, not `CommandSettings`.** This is **System.CommandLine**, not Spectre.Console.Cli — define `Option<T>`/`Argument<T>` locals, add them to the `Command`, read them via `parseResult.GetValue(...)` inside `cmd.SetAction(async (parseResult, ct) => ...)`. There is no settings POCO.
- **Handlers carry no business logic.** Each `INotificationHandler<T>` only mutates the singleton `ProgressState` (a state transition or counter bump). `ProgressState` is thread-safe by construction (`Interlocked`/`Volatile`/concurrent collections) because handlers fire from pipeline worker threads. New event in Core → add a thin handler here; don't compute anything.
- **`AddMediator()` is called in `CliBuilder`, not `AddArius`** — so the Mediator source generator runs in *this* assembly and discovers handlers in both Arius.Core and Arius.Cli. New handler types are picked up automatically.
- **Live display is a pure function of state.** `BuildDisplay(ProgressState)` returns an `IRenderable`; the responsive poll loop is `AnsiConsole.Live(...).StartAsync(ctx => while (!task.IsCompleted) { ctx.UpdateTarget(BuildDisplay(state)); await Task.WhenAny(task, Task.Delay(100, ct)); })`. Keep render logic in `BuildDisplay`; never read blob/repo state from it.
- **Non-interactive fallback.** Live display is gated on `AnsiConsole.Console.Profile.Capabilities.Interactive`; otherwise just `await mediator.Send(...)` and print the summary. Preserve both paths.
- **`ls` streams — no table buffering.** Consume `mediator.CreateStream(new ListQuery(...))` with `await foreach` and `AnsiConsole.MarkupLine` per row so it stays memory-bounded for millions of entries. `ls` deliberately uses **no console `Recorder`** (only its summary is logged); other verbs wrap output in a `Recorder` whose text is flushed to the audit log in `finally`.
- Errors: catch `PreflightException` around the `serviceProviderFactory(...)` call and map `PreflightErrorKind` to a friendly message; always `Markup.Escape(...)` interpolated user/error strings.

## Build & test
- Build: `dotnet build src/Arius.Cli/Arius.Cli.csproj` (net10.0). Contract changes in Core ripple here — see the root AGENTS.md note on building the full solution.
- Tests: `Arius.Cli.Tests` (TUnit; `InternalsVisibleTo` is set). Run them per the TUnit instructions in the root AGENTS.md (use `--treenode-filter`, not `--filter`). `BuildRootCommand` takes an optional `serviceProviderFactory` so tests inject mock handlers without touching Azure.
