# CLI Spec

## Purpose

Defines the command-line interface for Arius, including verbs, options, option resolution, and DI handler registration.

## Requirements

### Requirement: CLI verbs
The system SHALL provide four CLI verbs using System.CommandLine: `archive`, `restore`, `ls`, `update`. Each verb (except `update`) SHALL accept common options: `--account` / `-a` (account name), `--key` / `-k` (account key), `--passphrase` / `-p` (optional, for encryption), `--container` / `-c` (blob container name). The `--account` option SHALL NOT be marked as required by System.CommandLine (it MAY be resolved from environment variables).

#### Scenario: Archive verb
- **WHEN** `arius archive /path/to/folder -a myaccount -k *** -c backup`
- **THEN** the system SHALL invoke the archive Mediator command with the provided options

#### Scenario: Restore verb
- **WHEN** `arius restore /photos/ -a myaccount -k *** -c backup`
- **THEN** the system SHALL invoke the restore Mediator command for the specified path

#### Scenario: Ls verb
- **WHEN** `arius ls -a myaccount -k *** -c backup`
- **THEN** the system SHALL invoke the ls Mediator command

#### Scenario: Long-form options accepted
- **WHEN** `arius ls --account myaccount --key *** --container backup`
- **THEN** the system SHALL invoke the ls Mediator command identically to the short-form equivalent

### Requirement: Archive-specific CLI options
The archive verb SHALL accept: `--tier` / `-t` (Hot/Cool/Cold/Archive, default Archive), `--remove-local` (delete binaries after archive), `--no-pointers` (skip pointer files). The tuning options `--small-file-threshold`, `--tar-target-size`, and `--dedup-cache-mb` SHALL NOT be exposed on the CLI; their defaults (1 MB, 64 MB, 512 MB respectively) SHALL be hardcoded internally.

#### Scenario: Custom tier
- **WHEN** `arius archive /path -t Hot -a a -k k -c c`
- **THEN** chunks SHALL be uploaded to Hot tier

#### Scenario: Default tier
- **WHEN** `arius archive /path -a a -k k -c c` (no `--tier` specified)
- **THEN** chunks SHALL be uploaded to Archive tier

#### Scenario: Remove local with no pointers rejected
- **WHEN** `arius archive /path --remove-local --no-pointers -a a -k k -c c`
- **THEN** the system SHALL reject the command with an error explaining the incompatibility and return exit code 1

### Requirement: Restore-specific CLI options
The restore verb SHALL accept: `-v` / `--version` (snapshot version, default latest), `--no-pointers` (skip pointer creation on restore), `--overwrite` (overwrite local files without prompting).

#### Scenario: Restore specific version with short flag
- **WHEN** `arius restore /photos/ -v 2026-03-21T140000.000Z -a a -k k -c c`
- **THEN** the system SHALL restore from the specified snapshot

#### Scenario: Restore specific version with long flag
- **WHEN** `arius restore /photos/ --version 2026-03-21T140000.000Z -a a -k k -c c`
- **THEN** the system SHALL restore from the specified snapshot

### Requirement: Ls-specific CLI options
The ls verb SHALL accept: `-v` / `--version` (snapshot version, default latest), `--prefix` (path prefix filter), `--filter` / `-f` (filename substring filter, case-insensitive).

#### Scenario: Ls with prefix and filter
- **WHEN** `arius ls --prefix photos/ -f .jpg -a a -k k -c c`
- **THEN** the system SHALL list files under `photos/` whose filename contains `.jpg`

### Requirement: Account key resolution
The CLI SHALL resolve the account key in the following order: (1) `--key` / `-k` CLI parameter, (2) `ARIUS_KEY` environment variable, (3) `Microsoft.Extensions.Configuration.UserSecrets` (hidden, developer use only). The key SHALL NEVER be logged or displayed in output. The `--key` option description SHALL NOT mention user secrets.

#### Scenario: Key from CLI parameter
- **WHEN** `-k` is provided on the command line
- **THEN** the system SHALL use that key for authentication

#### Scenario: Key from environment variable
- **WHEN** `-k` is not provided but `ARIUS_KEY` environment variable is set
- **THEN** the system SHALL use the environment variable value for authentication

#### Scenario: Key from user secrets
- **WHEN** neither `-k` nor `ARIUS_KEY` is available but a user secret is configured
- **THEN** the system SHALL resolve the key from user secrets

#### Scenario: Key not found
- **WHEN** no key is available from any source
- **THEN** the system SHALL report an error and exit with exit code 1

### Requirement: Account name resolution
The CLI SHALL resolve the account name in the following order: (1) `--account` / `-a` CLI parameter, (2) `ARIUS_ACCOUNT` environment variable. If neither is available, the system SHALL report an error and exit.

#### Scenario: Account from CLI parameter
- **WHEN** `-a myaccount` is provided on the command line
- **THEN** the system SHALL use `myaccount` as the storage account name

#### Scenario: Account from environment variable
- **WHEN** `-a` is not provided but `ARIUS_ACCOUNT=myaccount` environment variable is set
- **THEN** the system SHALL use `myaccount` as the storage account name

#### Scenario: Account not found
- **WHEN** neither `-a` nor `ARIUS_ACCOUNT` is available
- **THEN** the system SHALL report an error and exit with exit code 1

#### Scenario: CLI flag overrides environment variable
- **WHEN** `-a override` is provided and `ARIUS_ACCOUNT=envaccount` is set
- **THEN** the system SHALL use `override` as the storage account name

### Requirement: DI handler registration
The system SHALL register command handlers by their `ICommandHandler<TCommand, TResult>` interface using explicit factory delegates that pass `accountName` and `containerName` as constructor arguments. The `AddArius()` extension method in `Arius.Core` SHALL encapsulate all DI registration. Handler registrations MUST be placed after `AddMediator()` to override the source generator's auto-registrations.

#### Scenario: Successful DI resolution
- **WHEN** `AddArius()` is called with valid account, key, passphrase, and container
- **THEN** resolving `IMediator` and sending any command SHALL NOT throw a DI resolution exception

#### Scenario: Handler receives string parameters
- **WHEN** `AddArius("myaccount", "mykey", null, "mycontainer")` is called
- **THEN** the `ArchivePipelineHandler` SHALL be constructed with `accountName="myaccount"` and `containerName="mycontainer"`

### Requirement: Archive progress display
The CLI SHALL display live progress during archive using Spectre.Console. The display SHALL show: scan progress (files found), hash progress (files hashed / total), dedup results (new vs. skipped), upload progress (chunks uploaded / total, bytes), and in-flight items (which files are currently being hashed and which chunks are being uploaded, with individual progress percentages). Tar bundle status (sealing, uploading, file count) SHALL also be displayed.

#### Scenario: Live progress during archive
- **WHEN** archiving 10,000 files
- **THEN** the CLI SHALL show a live-updating Spectre.Console display with aggregate counters and in-flight file names

#### Scenario: In-flight visibility
- **WHEN** 4 files are being hashed and 3 chunks are being uploaded concurrently
- **THEN** the display SHALL list each in-flight item with its name and progress percentage

### Requirement: Restore cost confirmation display
The CLI SHALL display a cost estimate before restoring and require interactive confirmation. The display SHALL include: files to restore, chunks categorized by availability (cached, Hot/Cool, needs rehydration), estimated costs (rehydration Standard vs. High, download egress), and a prompt for rehydration priority and proceed/cancel.

#### Scenario: Cost confirmation flow
- **WHEN** a restore requires 62 archive-tier chunks
- **THEN** the CLI SHALL display rehydration cost estimates for both Standard and High Priority, prompt for priority selection, then prompt for proceed/cancel

### Requirement: Restore progress display
The CLI SHALL display progress during the restore download phase. The display SHALL show: files restored from available chunks, bytes downloaded, and a summary on exit indicating how many files remain pending rehydration.

#### Scenario: Partial restore progress
- **WHEN** 500 files are available for immediate restore
- **THEN** the CLI SHALL show download progress and report "875 files remaining (pending rehydration)" on exit

### Requirement: Streaming progress events from Core
Arius.Core SHALL emit progress events via Mediator notifications. Event types SHALL include: FileScanned, FileHashing (with byte progress), FileHashed (with dedup result), ChunkUploading (with byte progress), ChunkUploaded, TarBundleSealing, TarBundleUploaded, SnapshotCreated, and equivalent restore events. The CLI SHALL subscribe to these events to drive the display.

#### Scenario: Progress event emission
- **WHEN** a file is hashed during archive
- **THEN** Core SHALL emit FileHashing events with bytes processed and FileHashed with the result

#### Scenario: CLI subscription
- **WHEN** Core emits a ChunkUploaded event
- **THEN** the CLI SHALL update the upload progress counter in the Spectre.Console display
