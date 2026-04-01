## MODIFIED Requirements

### Requirement: CLI verbs
The system SHALL provide four CLI verbs using System.CommandLine: `archive`, `restore`, `ls`, `update`. Each verb (except `update`) SHALL accept common options: `--account` / `-a` (account name), `--key` / `-k` (account key, optional — omit to use Azure CLI login), `--passphrase` / `-p` (optional, for encryption), `--container` / `-c` (blob container name). The `--account` option SHALL NOT be marked as required by System.CommandLine (it MAY be resolved from environment variables). The `--key` option SHALL NOT be marked as required (it MAY be omitted to fall back to Azure CLI identity-based authentication).

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

#### Scenario: Archive verb without key (az login)
- **WHEN** `arius archive /path/to/folder -a myaccount -c backup` (no `--key`) and user is logged in via `az login` with appropriate RBAC
- **THEN** the system SHALL authenticate via `AzureCliCredential` and invoke the archive Mediator command

#### Scenario: Ls verb without key (az login)
- **WHEN** `arius ls -a myaccount -c backup` (no `--key`) and user is logged in via `az login` with appropriate RBAC
- **THEN** the system SHALL authenticate via `AzureCliCredential` and invoke the ls Mediator command

### Requirement: Account key resolution
The CLI SHALL resolve the account key in the following order: (1) `--key` / `-k` CLI parameter, (2) `ARIUS_KEY` environment variable, (3) `Microsoft.Extensions.Configuration.UserSecrets` (hidden, developer use only). If no key is found from any source, the system SHALL NOT report an error; instead, credential resolution SHALL fall back to `AzureCliCredential` (see `azure-cli-auth` spec). The key SHALL NEVER be logged or displayed in output. The `--key` option description SHALL be `"Azure Storage account key (omit to use Azure CLI login)"` and SHALL NOT mention user secrets.

#### Scenario: Key from CLI parameter
- **WHEN** `-k` is provided on the command line
- **THEN** the system SHALL use that key for `StorageSharedKeyCredential` authentication

#### Scenario: Key from environment variable
- **WHEN** `-k` is not provided but `ARIUS_KEY` environment variable is set
- **THEN** the system SHALL use the environment variable value for `StorageSharedKeyCredential` authentication

#### Scenario: Key from user secrets
- **WHEN** neither `-k` nor `ARIUS_KEY` is available but a user secret is configured
- **THEN** the system SHALL resolve the key from user secrets for `StorageSharedKeyCredential` authentication

#### Scenario: Key not found — fallback to AzureCliCredential
- **WHEN** no key is available from any source (CLI flag, env var, or user secrets)
- **THEN** the system SHALL fall back to `AzureCliCredential` instead of reporting an error

### Requirement: DI handler registration
The system SHALL register command handlers by their `ICommandHandler<TCommand, TResult>` interface using explicit factory delegates that pass `accountName` and `containerName` as constructor arguments. The `AddArius()` extension method in `Arius.Core` SHALL encapsulate all DI registration. Handler registrations MUST be placed after `AddMediator()` to override the source generator's auto-registrations. The service provider factory delegate SHALL be `Func<string, string?, string?, string, PreflightMode, Task<IServiceProvider>>` (account name, optional key, optional passphrase, container name, preflight mode) and SHALL be awaited by the calling verb.

#### Scenario: Successful DI resolution with key
- **WHEN** the factory is called with valid account, key, passphrase, and container
- **THEN** resolving `IMediator` and sending any command SHALL NOT throw a DI resolution exception

#### Scenario: Successful DI resolution without key (az login)
- **WHEN** the factory is called with valid account, null key, passphrase, and container, and the user is logged in via `az login` with appropriate RBAC
- **THEN** resolving `IMediator` and sending any command SHALL NOT throw a DI resolution exception

#### Scenario: Handler receives string parameters
- **WHEN** the factory is called with `accountName="myaccount"` and `containerName="mycontainer"`
- **THEN** the `ArchivePipelineHandler` SHALL be constructed with `accountName="myaccount"` and `containerName="mycontainer"`

## REMOVED Requirements

### Requirement: FluentValidation dependency
**Reason**: The `FluentValidation` NuGet package is referenced in `Arius.Core.csproj` but has zero usage (no `using` statements, no validator classes, no calls). Removed as dead weight.
**Migration**: None required — no code depends on it.

### Requirement: FluentResults dependency
**Reason**: The `FluentResults` NuGet package is referenced in `Arius.Core.csproj` but has zero usage (no `using` statements, no Result types from FluentResults, no calls). Removed as dead weight.
**Migration**: None required — no code depends on it.
