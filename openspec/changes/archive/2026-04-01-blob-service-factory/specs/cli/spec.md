## MODIFIED Requirements

### Requirement: Account key resolution
The CLI SHALL resolve the account key in the following order: (1) `--key` / `-k` CLI parameter, (2) `ARIUS_KEY` environment variable, (3) `Microsoft.Extensions.Configuration.UserSecrets` (hidden, developer use only). The key SHALL NEVER be logged or displayed in output. The `--key` option description SHALL NOT mention user secrets. Credential resolution (key lookup and fallback to `AzureCliCredential`) SHALL remain in `Arius.Cli`. The resolved credential SHALL be passed to `BlobServiceFactory.CreateAsync` as either a `StorageSharedKeyCredential` or a `TokenCredential`.

#### Scenario: Key from CLI parameter
- **WHEN** `-k` is provided on the command line
- **THEN** the system SHALL create a `StorageSharedKeyCredential` and pass it to `BlobServiceFactory.CreateAsync`

#### Scenario: Key from environment variable
- **WHEN** `-k` is not provided but `ARIUS_KEY` environment variable is set
- **THEN** the system SHALL create a `StorageSharedKeyCredential` from the environment variable and pass it to `BlobServiceFactory.CreateAsync`

#### Scenario: Key from user secrets
- **WHEN** neither `-k` nor `ARIUS_KEY` is available but a user secret is configured
- **THEN** the system SHALL resolve the key from user secrets, create a `StorageSharedKeyCredential`, and pass it to `BlobServiceFactory.CreateAsync`

#### Scenario: No key available — fallback to AzureCliCredential
- **WHEN** no key is available from any source
- **THEN** the system SHALL create an `AzureCliCredential` and pass it to `BlobServiceFactory.CreateAsync`

### Requirement: DI handler registration
The system SHALL register command handlers by their `ICommandHandler<TCommand, TResult>` interface using explicit factory delegates that pass `accountName` and `containerName` as constructor arguments. The `AddArius()` extension method in `Arius.Core` SHALL encapsulate all DI registration. Handler registrations MUST be placed after `AddMediator()` to override the source generator's auto-registrations. `CliBuilder.BuildProductionServices` SHALL delegate blob service construction and preflight validation to `BlobServiceFactory.CreateAsync` and SHALL only be responsible for credential resolution, calling the factory, and wiring the DI container with the returned `IBlobStorageService`.

#### Scenario: Successful DI resolution
- **WHEN** `AddArius()` is called with a valid `IBlobStorageService` from `BlobServiceFactory.CreateAsync`
- **THEN** resolving `IMediator` and sending any command SHALL NOT throw a DI resolution exception

#### Scenario: BuildProductionServices delegates to factory
- **WHEN** `BuildProductionServices` is called with an account name, key, passphrase, container, and preflight mode
- **THEN** it SHALL resolve the credential, call `BlobServiceFactory.CreateAsync`, and build the DI container with the returned service

## ADDED Requirements

### Requirement: CLI formats PreflightException from structured fields
The CLI verb catch blocks SHALL format user-facing error messages by switching on `PreflightException.ErrorKind` and using the structured fields (`AccountName`, `ContainerName`, `AuthMode`, `StatusCode`) rather than displaying `PreflightException.Message` directly. Each verb SHALL format messages appropriate to its preflight mode (e.g., archive suggests "Storage Blob Data Contributor" role, restore/ls suggest "Storage Blob Data Reader" role). The CLI SHALL NOT have direct dependencies on Azure SDK namespaces (`Azure.Storage`, `Azure.Identity`, `Azure.Core`, `Azure`); all Azure interactions SHALL be mediated through `Arius.AzureBlob` types.

#### Scenario: ContainerNotFound error formatted
- **WHEN** a verb catches `PreflightException` with `ErrorKind = ContainerNotFound`
- **THEN** the CLI SHALL display a message including the container name and account name from the structured fields

#### Scenario: AccessDenied with key auth formatted
- **WHEN** a verb catches `PreflightException` with `ErrorKind = AccessDenied` and `AuthMode = "key"`
- **THEN** the CLI SHALL display a message suggesting the account key may be incorrect

#### Scenario: AccessDenied with token auth formatted for archive
- **WHEN** the archive verb catches `PreflightException` with `ErrorKind = AccessDenied` and `AuthMode = "token"`
- **THEN** the CLI SHALL display a message suggesting the "Storage Blob Data Contributor" RBAC role

#### Scenario: AccessDenied with token auth formatted for restore
- **WHEN** the restore verb catches `PreflightException` with `ErrorKind = AccessDenied` and `AuthMode = "token"`
- **THEN** the CLI SHALL display a message suggesting the "Storage Blob Data Reader" RBAC role

#### Scenario: CredentialUnavailable error formatted
- **WHEN** a verb catches `PreflightException` with `ErrorKind = CredentialUnavailable`
- **THEN** the CLI SHALL display a message listing the credential resolution options (--key, ARIUS_KEY, user secrets, az login)

#### Scenario: CLI has no direct Azure SDK dependencies
- **WHEN** `Arius.Cli` is analyzed for type dependencies
- **THEN** no class in `Arius.Cli` SHALL depend on types in `Azure.Storage`, `Azure.Identity`, or `Azure.Core` namespaces
