## ADDED Requirements

### Requirement: CLI verbs
The system SHALL provide three CLI verbs using System.CommandLine: `archive`, `restore`, `ls`. Each verb SHALL accept common options: `--account` (account name), `--key` (account key), `--passphrase` (optional, for encryption), `--container` (blob container name).

#### Scenario: Archive verb
- **WHEN** `arius archive /path/to/folder --account myaccount --key *** --container backup`
- **THEN** the system SHALL invoke the archive Mediator command with the provided options

#### Scenario: Restore verb
- **WHEN** `arius restore /photos/ --account myaccount --key *** --container backup`
- **THEN** the system SHALL invoke the restore Mediator command for the specified path

#### Scenario: Ls verb
- **WHEN** `arius ls --account myaccount --key *** --container backup`
- **THEN** the system SHALL invoke the ls Mediator command

### Requirement: Archive-specific CLI options
The archive verb SHALL accept: `--tier` (Hot/Cool/Cold/Archive, default Archive), `--remove-local` (delete binaries after archive), `--no-pointers` (skip pointer files), `--small-file-threshold` (default 1 MB), `--tar-target-size` (default 64 MB), `--dedup-cache-mb` (LRU cache size, default 512).

#### Scenario: Custom tier
- **WHEN** `arius archive /path --tier Hot --account a --key k --container c`
- **THEN** chunks SHALL be uploaded to Hot tier

#### Scenario: Remove local with no pointers rejected
- **WHEN** `arius archive /path --remove-local --no-pointers --account a --key k --container c`
- **THEN** the system SHALL reject the command with an error explaining the incompatibility

### Requirement: Restore-specific CLI options
The restore verb SHALL accept: `-v` (snapshot version, default latest), `--no-pointers` (skip pointer creation on restore), `--overwrite` (overwrite local files without prompting).

#### Scenario: Restore specific version
- **WHEN** `arius restore /photos/ -v 2026-03-21T140000.000Z --account a --key k --container c`
- **THEN** the system SHALL restore from the specified snapshot

### Requirement: Ls-specific CLI options
The ls verb SHALL accept: `-v` (snapshot version, default latest), `--prefix` (path prefix filter), `--filter` (filename substring filter).

#### Scenario: Ls with prefix and filter
- **WHEN** `arius ls --prefix photos/ --filter .jpg --account a --key k --container c`
- **THEN** the system SHALL list files under `photos/` whose filename contains `.jpg`

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

### Requirement: Account key resolution
The CLI SHALL resolve the account key from: (1) `--key` CLI parameter, (2) `Microsoft.Extensions.Configuration.UserSecrets` during local development. The key SHALL NEVER be logged or displayed in output.

#### Scenario: Key from CLI parameter
- **WHEN** `--key` is provided on the command line
- **THEN** the system SHALL use that key for authentication

#### Scenario: Key from user secrets
- **WHEN** `--key` is not provided but a user secret is configured
- **THEN** the system SHALL resolve the key from user secrets

#### Scenario: Key not found
- **WHEN** no key is available from any source
- **THEN** the system SHALL report an error and exit
