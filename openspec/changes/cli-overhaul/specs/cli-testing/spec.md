## ADDED Requirements

### Requirement: CLI test project
The system SHALL have an `Arius.Cli.Tests` test project using TUnit, NSubstitute, and Shouldly that tests CLI command parsing, option validation, and DI wiring without requiring Azure credentials or network access.

#### Scenario: Test project builds and runs
- **WHEN** `dotnet test src/Arius.Cli.Tests/Arius.Cli.Tests.csproj`
- **THEN** all tests SHALL pass without requiring any environment variables, Azure credentials, or network access

### Requirement: Mock command handler injection
Tests SHALL register NSubstitute mock implementations of `ICommandHandler<TCommand, TResult>` after `AddMediator()` to intercept command dispatch. The mock handlers SHALL capture the command objects passed to `mediator.Send()` for assertion.

#### Scenario: Mock handler captures archive command
- **WHEN** a test invokes the CLI with `archive /tmp -a acct -k key -c ctr`
- **THEN** the mock `ICommandHandler<ArchiveCommand, ArchiveResult>` SHALL receive an `ArchiveCommand` with `ArchiveOptions.RootDirectory` containing `/tmp`

#### Scenario: Mock handler captures restore command
- **WHEN** a test invokes the CLI with `restore /tmp -a acct -k key -c ctr -v 2026-01-01`
- **THEN** the mock `ICommandHandler<RestoreCommand, RestoreResult>` SHALL receive a `RestoreCommand` with `RestoreOptions.Version` equal to `2026-01-01`

#### Scenario: Mock handler captures ls command
- **WHEN** a test invokes the CLI with `ls -a acct -k key -c ctr --prefix photos/ -f .jpg`
- **THEN** the mock `ICommandHandler<LsCommand, LsResult>` SHALL receive an `LsCommand` with `LsOptions.Prefix` equal to `photos/` and `LsOptions.Filter` equal to `.jpg`

### Requirement: Archive command parsing tests
Tests SHALL verify that the archive command correctly parses all options, applies defaults, and enforces validation rules.

#### Scenario: Archive with all options
- **WHEN** `archive /data -a acct -k key -c ctr -t Hot --remove-local`
- **THEN** the captured `ArchiveOptions` SHALL have `UploadTier=Hot`, `RemoveLocal=true`, `NoPointers=false`

#### Scenario: Archive defaults
- **WHEN** `archive /data -a acct -k key -c ctr`
- **THEN** the captured `ArchiveOptions` SHALL have `UploadTier=Archive`, `RemoveLocal=false`, `NoPointers=false`

#### Scenario: Archive remove-local plus no-pointers rejected
- **WHEN** `archive /data -a acct -k key -c ctr --remove-local --no-pointers`
- **THEN** the CLI SHALL return exit code 1 without invoking the handler

### Requirement: Restore command parsing tests
Tests SHALL verify that the restore command correctly parses all options and applies defaults.

#### Scenario: Restore with version
- **WHEN** `restore /data -a acct -k key -c ctr -v 2026-03-21T140000.000Z`
- **THEN** the captured `RestoreOptions` SHALL have `Version="2026-03-21T140000.000Z"`

#### Scenario: Restore defaults
- **WHEN** `restore /data -a acct -k key -c ctr`
- **THEN** the captured `RestoreOptions` SHALL have `Version=null`, `NoPointers=false`, `Overwrite=false`

### Requirement: Ls command parsing tests
Tests SHALL verify that the ls command correctly parses all options and applies defaults.

#### Scenario: Ls with all filters
- **WHEN** `ls -a acct -k key -c ctr -v 2026-01-01 --prefix docs/ -f .pdf`
- **THEN** the captured `LsOptions` SHALL have `Version="2026-01-01"`, `Prefix="docs/"`, `Filter=".pdf"`

#### Scenario: Ls defaults
- **WHEN** `ls -a acct -k key -c ctr`
- **THEN** the captured `LsOptions` SHALL have `Version=null`, `Prefix=null`, `Filter=null`

### Requirement: Account and key resolution tests
Tests SHALL verify the environment variable fallback chain for account name and key resolution.

#### Scenario: CLI flag overrides env var for account
- **WHEN** `ARIUS_ACCOUNT=envacct` is set and CLI is invoked with `-a cliacct -k key -c ctr`
- **THEN** the system SHALL use `cliacct` as the account name

#### Scenario: Env var used when CLI flag omitted for account
- **WHEN** `ARIUS_ACCOUNT=envacct` is set and CLI is invoked with `-k key -c ctr` (no `-a`)
- **THEN** the system SHALL use `envacct` as the account name

#### Scenario: Missing account from all sources
- **WHEN** no `-a` flag and no `ARIUS_ACCOUNT` env var
- **THEN** the CLI SHALL return exit code 1 without invoking the handler

#### Scenario: Env var used when CLI flag omitted for key
- **WHEN** `ARIUS_KEY=envkey` is set and CLI is invoked with `-a acct -c ctr` (no `-k`)
- **THEN** the system SHALL use `envkey` as the account key

#### Scenario: Missing key from all sources
- **WHEN** no `-k` flag, no `ARIUS_KEY` env var, and no user secrets configured
- **THEN** the CLI SHALL return exit code 1 without invoking the handler
