# cli

## Purpose
Defines the command-line interface surface, rendering, repository connection options, and output modes for the Arius CLI.

## Requirements

### Requirement: Restic-like command surface
The CLI SHALL provide commands mirroring restic's interface: `init`, `backup`, `restore`, `snapshots`, `ls`, `find`, `forget`, `prune`, `check`, `diff`, `cat`, `stats`, `tag`, `key`, `repair`, `version`.

#### Scenario: Help output
- **WHEN** user runs `arius --help`
- **THEN** the system displays all available commands with brief descriptions

### Requirement: System.CommandLine parsing
The CLI SHALL use System.CommandLine for command parsing, argument binding, option handling, and subcommand dispatch.

### Requirement: Spectre.Console rendering
The CLI SHALL use Spectre.Console for rich terminal output including tables, progress bars, live updates, and styled text.

#### Scenario: Snapshots table
- **WHEN** user runs `arius snapshots`
- **THEN** snapshots are displayed in a formatted Spectre.Console table

#### Scenario: Backup progress
- **WHEN** a backup is in progress
- **THEN** Spectre.Console renders a live progress display with file count, bytes processed, and upload speed

### Requirement: Repository connection options
The CLI SHALL accept repository location via `--repo` flag or `ARIUS_REPOSITORY` environment variable, and passphrase via prompt, `--password-file`, or `ARIUS_PASSWORD` environment variable.

#### Scenario: Repo via flag
- **WHEN** user runs `arius --repo azure://account/container snapshots`
- **THEN** the system connects to the specified Azure container

#### Scenario: Repo via environment
- **WHEN** `ARIUS_REPOSITORY` is set and user runs `arius snapshots`
- **THEN** the system connects to the container specified in the environment variable

#### Scenario: Password prompt
- **WHEN** no password is provided via flag or environment
- **THEN** the CLI prompts the user for a passphrase (hidden input)

### Requirement: JSON output
The CLI SHALL support `--json` flag for machine-readable JSON output on all listing commands.

#### Scenario: JSON snapshots
- **WHEN** user runs `arius snapshots --json`
- **THEN** snapshots are output as a JSON array

### Requirement: Backup tier selection
The `backup` command SHALL accept an optional `--tier` argument (values: `hot`, `cool`, `cold`, `archive`; default: `archive`) to control the Azure Blob Storage access tier for uploaded data packs.

#### Scenario: Backup with default Archive tier
- **WHEN** user runs `backup /path/to/data` without `--tier`
- **THEN** data packs are uploaded to Azure with the Archive access tier

#### Scenario: Backup with explicit tier
- **WHEN** user runs `backup --tier hot /path/to/data`
- **THEN** data packs are uploaded to Azure with the Hot access tier

### Requirement: Streaming CLI output
Long-running operations SHALL render progress using Spectre.Console's `Live`, `Progress`, or `Status` contexts, consuming `IAsyncEnumerable` from the Mediator handlers.

#### Scenario: Restore progress
- **WHEN** a restore is in progress
- **THEN** the CLI renders a live display showing rehydration progress and file restoration progress concurrently

### Requirement: Confirmation prompts
Destructive or costly operations (forget, prune, restore with rehydration) SHALL prompt for user confirmation unless `--yes` or `--dry-run` is specified.

#### Scenario: Forget confirmation
- **WHEN** user runs `forget --keep-last 3` without `--yes`
- **THEN** the system shows which snapshots will be removed and prompts for confirmation

#### Scenario: Skip confirmation
- **WHEN** user runs `forget --keep-last 3 --yes`
- **THEN** the operation proceeds without prompting
