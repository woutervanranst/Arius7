## MODIFIED Requirements

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

## ADDED Requirements

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

## REMOVED Requirements

### Requirement: Docker support
**Reason**: Docker support is being dropped from the CLI. The Dockerfile and Docker-related documentation SHALL be removed.
**Migration**: Use native platform binaries instead (Windows, Linux, macOS).
