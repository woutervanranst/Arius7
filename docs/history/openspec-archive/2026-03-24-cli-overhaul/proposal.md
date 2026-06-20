## Why

The CLI has zero test coverage, a confirmed DI wiring bug that crashes on every command invocation, no short option aliases, and exposes internal tuning parameters that confuse end users. The Mediator source generator auto-registers handlers by concrete type, bypassing the manual factory registrations in `BuildServices` and failing to resolve `string` constructor parameters. This needs to be fixed and made testable.

## What Changes

- **Fix DI wiring bug**: Register handlers by `ICommandHandler<,>` interface with explicit factory delegates (not by concrete type), so Mediator's source-generated dispatch resolves the factory-built instances that properly pass `accountName`/`containerName` strings
- **Extract `AddArius()` extension method** in `Arius.Core` that encapsulates all DI registration (blob storage, encryption, chunk index, handlers), making it reusable and overridable in tests
- **Add short option aliases**: `-a` (account), `-k` (key), `-p` (passphrase), `-c` (container), `-t` (tier), `-f` (filter), `--version`/`-v` (snapshot version)
- **Remove exposed tuning options**: Drop `--small-file-threshold`, `--tar-target-size`, `--dedup-cache-mb` from the CLI surface; hardcode sensible defaults internally
- **Add `ARIUS_ACCOUNT` / `ARIUS_KEY` environment variable fallback**: Resolution order: CLI flag > env var > (hidden) user secrets > error. `--container` and `--passphrase` remain CLI-only.
- **Remove Docker support**: Drop `Dockerfile` and Docker sections from README
- **Fix `--key` description**: Remove "(or omit to use user secrets)" developer-facing text
- **BREAKING**: `--account` becomes optional on CLI when `ARIUS_ACCOUNT` env var is set
- **Create CLI test suite**: Tests invoke System.CommandLine parsing with mock `ICommandHandler<,>` implementations (via NSubstitute) that capture command objects for assertion, verifying correct option parsing, validation guards, defaults, and env var resolution across all four commands (archive, restore, ls, update)

## Capabilities

### New Capabilities
- `cli-testing`: Test infrastructure for CLI command parsing, option validation, and DI wiring using mock command handlers

### Modified Capabilities
- `cli`: Short option aliases, removed tuning options, env var fallback for account/key, fixed DI handler registration, removed Docker support

## Impact

- `src/Arius.Cli/Program.cs`: Refactored option definitions, simplified archive options, new env var resolution in key/account helpers, `BuildServices` replaced by call to `AddArius()`
- `src/Arius.Core/`: New `ServiceCollectionExtensions.AddArius()` extension method; handler registrations move from CLI to Core
- `src/Arius.Cli.Tests/`: New test project with tests for all commands
- `src/Dockerfile`: Removed
- `README.md`: Docker sections removed, CLI usage examples updated with short aliases
- `openspec/changes/archive/.../specs/cli/spec.md`: Updated requirements for new option surface
