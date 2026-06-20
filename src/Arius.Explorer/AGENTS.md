# Arius.Explorer — agent contract

Cross-cutting rules (think-before-coding, simplicity, testing workflow, code style, domain
language, the documentation map) live in the root [`../../AGENTS.md`](../../AGENTS.md) — they
apply here and are **not** repeated. This file is only what is true of *this* project.

- **How the shell is wired** (startup, DI, the per-repository session, the root placeholder
  graph): [`../../docs/design/hosts/explorer.md`](../../docs/design/hosts/explorer.md).
- **Per-repository provider lifetimes / `AddArius`:** [`../../docs/design/cross-cutting/service-lifetimes.md`](../../docs/design/cross-cutting/service-lifetimes.md).
- **User-facing behaviour:** [`../../docs/guide/explorer.md`](../../docs/guide/explorer.md). Terms: [`../../docs/glossary.md`](../../docs/glossary.md).

## What this is

The **Windows-only WPF/MVVM desktop** host for *browsing* and *restoring* a repository
(`net10.0-windows`, `UseWPF`, `WinExe`). Read/restore only — archiving stays in the CLI.
A thin GUI over `Arius.Core`: it touches Azure only via `IBlobServiceFactory`, drives Core
exclusively through `IMediator`, and never calls handlers or the Azure SDK directly.

## Layout (vertical slices — group by feature, not by type)

- `ChooseRepository/` — modal to pick/enter a repository; detects Arius containers via the
  `ContainerNamesQuery` stream (debounced through `System.Reactive` on credential change).
- `RepositoryExplorer/` — main window; browses via `ListQuery`, lazily resolves rehydration
  state via `ChunkHydrationStatusQuery`, restores via `RestoreCommand`.
- `Settings/` — `RepositoryOptions` (the persisted record) + `ApplicationSettings` /
  `IRecentRepositoryManager` (recent-repo list).
- `Infrastructure/RepositorySession.cs` — the per-repository Core graph (below).
- `Shared/` — `Services/` (`IDialogService`), `Converters/`, `Extensions/` (DPAPI).
- `Program.cs` builds the generic `Host`; `App.xaml.cs` is the WPF entry. The host container
  holds only **shell** services plus a *root* placeholder Core graph (`NullBlobContainerService`)
  so `ChooseRepository` has a live `IMediator` before any repository is connected.

## Project-local invariants — read before editing

- **`RepositorySession` owns exactly one live Core graph per repository.** `ConnectAsync` is
  swap-on-connect: it **must** `DisposeCurrentProvider()` *before* building the next child
  `ServiceProvider`. Never let two live graphs coexist for one repo — duplicate graphs split
  cache/validation state, and `ChunkIndexService` is single-shot after flush. The child
  provider is built from a `ServiceCollection` + `AddMediator()` + `AddArius(...)`; the root
  provider is its parent only for `ILoggerFactory` and `IBlobServiceFactory`.
- **Global unhandled-exception handlers are load-bearing, not belt-and-suspenders.** Most Core
  work runs fire-and-forget off UI events (`async void` node selection, `_ = LoadRepositoryAsync()`),
  so `App.SetupGlobalExceptionHandlers` traps `DispatcherUnhandledException`,
  `AppDomain.UnhandledException`, and `TaskScheduler.UnobservedTaskException` (log + message box,
  mark handled). Don't remove them; if you add fire-and-forget work, it must self-handle too.

## Conventions

- **MVVM via CommunityToolkit.Mvvm.** `[ObservableProperty]` / `[RelayCommand]` (async when it
  calls Core), `partial void On…Changed`, `[NotifyCanExecuteChangedFor]` for command gating.
  ViewModels are DI-registered (`AddTransient`); keep logic out of `.xaml.cs` code-behind.
- **Marshal to the UI thread explicitly** when updating bound state from a Core stream — capture
  `SynchronizationContext` and `Post` (see `ChooseRepositoryViewModel`); long lists stream in as
  results arrive and use `CancellationTokenSource` swap-and-cancel (see `LoadNodeContentAsync`).
- **Credentials never persist in clear text.** `RepositoryOptions` stores `AccountKeyProtected` /
  `PassphraseProtected`; `Shared/Extensions/DataProtectionExtensions` wraps Windows **DPAPI**
  (`ProtectedData`, `CurrentUser`) and falls back to plaintext off-Windows. The recent-repo list
  is the Windows user-settings store (`ApplicationSettingsBase`, strong-named for ClickOnce).

## Build & test (Windows only)

- `dotnet build src/Arius.Explorer/Arius.Explorer.csproj` and `Arius.Explorer.Tests` build/run
  **only on Windows** (`net10.0-windows`). On macOS/Linux skip them; verify Core contract
  changes against the cross-platform projects instead.
- Tests mirror this folder tree. Run with TUnit conventions from the root contract
  (`--treenode-filter`, not `--filter`).
