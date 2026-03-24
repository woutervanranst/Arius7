## Context

The CLI (`src/Arius.Cli/Program.cs`, 610 lines) is a System.CommandLine application with four commands: `archive`, `restore`, `ls`, `update`. All logic lives in top-level statements with two `static` helper methods (`ResolveAccountKey`, `BuildServices`) and inline `SetAction` lambdas.

The Mediator source generator (`martinothamar/Mediator` v3.0.2) auto-registers all `ICommandHandler<,>` implementations at compile time. The generated `AddMediator()` registers handlers by concrete type (e.g., `ArchivePipelineHandler`) using `TryAdd`, then registers the interface mapping (e.g., `ICommandHandler<ArchiveCommand, ArchiveResult>`) pointing to the same concrete type. When the DI container tries to construct the handler, it cannot resolve the `string accountName` and `string containerName` constructor parameters — causing a crash on every command invocation.

The manual factory registrations in `BuildServices` (which correctly pass these strings) are dead code: Mediator's `CommandHandlerWrapper.Init()` resolves via `sp.GetRequiredService<ICommandHandler<TCommand, TResponse>>()`, which uses the auto-generated registration, not the manual one.

## Goals / Non-Goals

**Goals:**
- Fix the DI crash so the CLI actually works
- Make the CLI testable by allowing handler replacement at the DI level
- Clean up the CLI surface: short aliases, remove internal tuning knobs, add env var support
- Achieve meaningful test coverage on CLI parsing, validation, and wiring

**Non-Goals:**
- Refactoring Core handler internals (only their DI registration changes)
- Adding new CLI commands or features
- Changing archive/restore/ls behavior
- E2E testing against Azure (existing E2E tests cover that)

## Decisions

### 1. Register handlers by interface with factory delegates

**Decision**: After `AddMediator()`, re-register each handler using `services.AddSingleton<ICommandHandler<TCommand, TResult>>(sp => new Handler(...))` with explicit factory delegates that pass the string parameters.

**Why**: MS DI `GetRequiredService<T>` returns the last registration when multiple exist. Since `AddMediator()` uses `services.Add()` (not `TryAdd`) for the interface registrations, a subsequent `services.AddSingleton<ICommandHandler<...>>(factory)` will be the one Mediator's `CommandHandlerWrapper` resolves.

**Alternative considered**: Removing string parameters from handler constructors and injecting a config object instead. Rejected because it would require changing all three handlers in Core and their tests for what is fundamentally a DI wiring issue.

### 2. `AddArius()` extension method in `Arius.Core`

**Decision**: Create `ServiceCollectionExtensions.AddArius(this IServiceCollection, string account, string key, string? passphrase, string container, long? cacheBudget)` in `Arius.Core` that:
1. Registers `IBlobStorageService`, `IEncryptionService`, `ChunkIndexService`
2. Calls `AddMediator()`
3. Re-registers all three handlers by their `ICommandHandler<,>` interface with factory delegates

**Why**: Centralizes DI registration in Core (where the handlers live), makes it reusable for future consumers (API), and creates a clear seam for test overrides.

**Test override pattern**: Tests call `AddMediator()` directly (needed for the generated `Mediator` class), then register mock `ICommandHandler<,>` instances. They never call `AddArius()` — the mocks override Mediator's auto-registrations the same way `AddArius()` does.

### 3. Test via System.CommandLine invocation with mock handlers

**Decision**: The test project references both `Arius.Cli` and `Arius.Core`. Tests build a `ServiceCollection`, call `AddMediator()`, register NSubstitute mocks for `ICommandHandler<ArchiveCommand, ArchiveResult>` etc., then invoke System.CommandLine parsing. The mock handlers capture the command objects passed to `mediator.Send()` for assertion.

**Why**: Tests the full parsing → validation → command construction → dispatch chain without needing Azure credentials or blob storage. The mock handler captures exactly what the CLI would send to Core.

**Key detail**: The `Program.cs` `BuildServices` method must be refactored so tests can inject their own `IServiceProvider`. The cleanest approach is to make `BuildServices` call `AddArius()` and have tests provide an alternative service configuration.

### 4. Environment variable resolution order

**Decision**: `CLI flag > ARIUS_ACCOUNT/ARIUS_KEY env var > user secrets > error`

**Why**: CLI flags are most explicit. Env vars are the standard for Docker/cron/CI. User secrets remain as a hidden dev convenience. This matches the `ARIUS_E2E_ACCOUNT`/`ARIUS_E2E_KEY` convention already in the test suite.

`--account` becomes non-required in System.CommandLine (with validation: must come from somewhere). `--container` and `--passphrase` remain CLI-only — container varies per invocation and passphrase is sensitive.

### 5. Short aliases and option cleanup

**Decision**:

| Option | Short | Notes |
|--------|-------|-------|
| `--account` | `-a` | |
| `--key` | `-k` | |
| `--passphrase` | `-p` | |
| `--container` | `-c` | |
| `--tier` | `-t` | archive only |
| `--filter` | `-f` | ls only |
| `-v` | `--version` | restore/ls, add long form |
| `--remove-local` | — | flag, no short |
| `--no-pointers` | — | flag, no short |
| `--overwrite` | — | flag, no short |
| `--prefix` | — | no short (`-p` taken by passphrase) |

Removed from CLI surface entirely (hardcoded defaults): `--small-file-threshold` (1 MB), `--tar-target-size` (64 MB), `--dedup-cache-mb` (512).

## Risks / Trade-offs

**[DI registration ordering]** → The approach depends on MS DI's "last registration wins" behavior for `GetRequiredService`. This is documented and stable behavior, but if Mediator changes its internal resolution strategy in a future version, the override could break. Mitigation: pin Mediator version; the test suite will catch any regression since it exercises the full DI chain.

**[Top-level statements testability]** → `Program.cs` uses top-level statements, making it impossible to directly reference `BuildServices` or command setup from tests. Mitigation: Extract `AddArius()` to Core; refactor `Program.cs` so the root command construction and service building are accessible (e.g., via a `public static` method on `AssemblyMarker` or a separate `CliBuilder` class in `Arius.Cli`).

**[BREAKING: `--account` no longer required]** → Users who rely on System.CommandLine's error message when `--account` is missing will now get a different error (from our validation logic instead of the parser). Mitigation: the custom error message is actually clearer ("No account provided. Use --account or set ARIUS_ACCOUNT environment variable.").

**[Removed options]** → Power users who tuned `--small-file-threshold` or `--tar-target-size` lose that ability. Mitigation: the defaults are sensible for all known use cases; these can be re-exposed later if needed.
