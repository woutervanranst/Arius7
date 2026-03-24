## 1. Core DI Infrastructure

- [x] 1.1 Create `src/Arius.Core/ServiceCollectionExtensions.cs` with `AddArius(this IServiceCollection, string account, string key, string? passphrase, string container, long? cacheBudget)` that registers `IBlobStorageService`, `IEncryptionService`, `ChunkIndexService`, calls `AddMediator()`, and re-registers all three handlers by `ICommandHandler<,>` interface with factory delegates
- [x] 1.2 Verify `AddArius()` compiles and the handler interface registrations come after `AddMediator()` (build check)

## 2. CLI Option Refactoring

- [x] 2.1 Add short aliases to common options: `--account`/`-a`, `--key`/`-k`, `--passphrase`/`-p`, `--container`/`-c`
- [x] 2.2 Add short aliases to verb-specific options: `--tier`/`-t`, `--filter`/`-f`, `-v`/`--version` (add `--version` long form)
- [x] 2.3 Remove `--small-file-threshold`, `--tar-target-size`, `--dedup-cache-mb` from the CLI surface; hardcode defaults (1 MB, 64 MB, 512 MB) where they are consumed
- [x] 2.4 Remove "(or omit to use user secrets)" from `--key` description
- [x] 2.5 Make `--account` non-required in System.CommandLine (validation moves to action handler)

## 3. Environment Variable Support

- [x] 3.1 Implement account name resolution: CLI flag > `ARIUS_ACCOUNT` env var > error; with clear error message when missing
- [x] 3.2 Implement key resolution: CLI flag > `ARIUS_KEY` env var > user secrets > error; with clear error message when missing
- [x] 3.3 Update `--key` to be non-required in System.CommandLine (validated in action handler like account)

## 4. Program.cs DI Wiring Fix

- [x] 4.1 Replace `BuildServices` body with call to `AddArius()` extension method, passing resolved account, key, passphrase, container, and hardcoded cache budget
- [x] 4.2 Refactor `Program.cs` so command setup and service building are accessible from tests (e.g., extract `BuildRootCommand` to a public static method or separate class)

## 5. Test Project Setup

- [x] 5.1 Create `src/Arius.Cli.Tests/Arius.Cli.Tests.csproj` (TUnit, NSubstitute, Shouldly; references `Arius.Cli` and `Arius.Core`)
- [x] 5.2 Create test helper/fixture that builds `ServiceCollection`, calls `AddMediator()`, registers NSubstitute mock `ICommandHandler<,>` implementations, and provides a method to invoke System.CommandLine parsing

## 6. CLI Parsing Tests

- [x] 6.1 Archive command tests: all options parsed, defaults applied, tier mapping, `--remove-local` + `--no-pointers` rejection (exit code 1)
- [x] 6.2 Restore command tests: version parsing (short `-v` and long `--version`), defaults, `--overwrite` and `--no-pointers` flags
- [x] 6.3 Ls command tests: `--prefix`, `--filter`/`-f`, version, defaults

## 7. Account/Key Resolution Tests

- [x] 7.1 Test CLI flag overrides env var for account
- [x] 7.2 Test env var used when CLI flag omitted for account
- [x] 7.3 Test missing account from all sources returns exit code 1
- [x] 7.4 Test env var used when CLI flag omitted for key
- [x] 7.5 Test missing key from all sources returns exit code 1

## 8. Docker Removal and Documentation

- [ ] 8.1 Delete `src/Dockerfile`
- [ ] 8.2 Remove Docker sections from `README.md`
- [ ] 8.3 Update CLI usage examples in `README.md` with short aliases
