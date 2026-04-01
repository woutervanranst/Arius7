# Azure CLI Auth Spec

## Purpose

Defines credential resolution, preflight connectivity checks, and user-friendly error messages for Azure Storage authentication in Arius CLI.

## Requirements

### Requirement: Credential resolution with Azure CLI fallback
The CLI SHALL resolve credentials in the following order of precedence: (1) `--key` / `-k` CLI parameter → `StorageSharedKeyCredential`, (2) `ARIUS_KEY` environment variable → `StorageSharedKeyCredential`, (3) `Microsoft.Extensions.Configuration.UserSecrets` → `StorageSharedKeyCredential`, (4) `AzureCliCredential` → `TokenCredential`. If a key is found at any of steps 1-3, the system SHALL use `StorageSharedKeyCredential`. If no key is found, the system SHALL fall back to `AzureCliCredential` from the `Azure.Identity` package. The system SHALL NOT use `DefaultAzureCredential`.

#### Scenario: Key from CLI flag takes precedence over az login
- **WHEN** `-k mykey` is provided on the command line and the user is logged in via `az login`
- **THEN** the system SHALL authenticate with `StorageSharedKeyCredential` using the provided key

#### Scenario: Key from env var takes precedence over az login
- **WHEN** `-k` is not provided, `ARIUS_KEY` is set, and the user is logged in via `az login`
- **THEN** the system SHALL authenticate with `StorageSharedKeyCredential` using the environment variable value

#### Scenario: Key from user secrets takes precedence over az login
- **WHEN** neither `-k` nor `ARIUS_KEY` is available, a user secret is configured, and the user is logged in via `az login`
- **THEN** the system SHALL authenticate with `StorageSharedKeyCredential` using the user secret value

#### Scenario: Fallback to AzureCliCredential when no key is found
- **WHEN** no key is available from CLI flag, environment variable, or user secrets
- **THEN** the system SHALL authenticate with `AzureCliCredential`

#### Scenario: AzureCliCredential used with correct account
- **WHEN** the system falls back to `AzureCliCredential` and `--account myaccount` is provided
- **THEN** the system SHALL construct a `BlobServiceClient` with the URI `https://myaccount.blob.core.windows.net` and the `AzureCliCredential` token credential

### Requirement: Preflight connectivity check
The CLI SHALL perform a preflight check against Azure Storage after constructing the `BlobServiceClient` and before building the DI container, on every invocation regardless of authentication mechanism. The factory SHALL accept a `PreflightMode` parameter (`ReadOnly` or `ReadWrite`). For `PreflightMode.ReadWrite` (archive), the system SHALL upload an empty blob named `.arius-preflight-probe` with `overwrite: true`, then delete it. For `PreflightMode.ReadOnly` (restore, ls), the system SHALL call `container.ExistsAsync()`. If the preflight check fails, the system SHALL throw a `PreflightException` with a user-friendly message.

#### Scenario: Archive preflight succeeds with valid key
- **WHEN** archiving with a valid account key
- **THEN** the system SHALL write and delete `.arius-preflight-probe` without error and proceed to build the DI container

#### Scenario: Archive preflight succeeds with az login and Contributor role
- **WHEN** archiving with `AzureCliCredential` and the user has `Storage Blob Data Contributor` role
- **THEN** the system SHALL write and delete `.arius-preflight-probe` without error and proceed

#### Scenario: Restore preflight succeeds with valid key
- **WHEN** restoring with a valid account key
- **THEN** the system SHALL call `container.ExistsAsync()` without error and proceed

#### Scenario: Restore preflight succeeds with az login and Reader role
- **WHEN** restoring with `AzureCliCredential` and the user has `Storage Blob Data Reader` role
- **THEN** the system SHALL call `container.ExistsAsync()` without error and proceed

#### Scenario: Ls preflight succeeds
- **WHEN** listing with valid credentials (key or token)
- **THEN** the system SHALL call `container.ExistsAsync()` without error and proceed

#### Scenario: Preflight detects wrong account key
- **WHEN** an incorrect account key is provided
- **THEN** the preflight SHALL throw `PreflightException` with a message indicating the key may be incorrect for the specified account

#### Scenario: Preflight detects container not found
- **WHEN** credentials are valid but the container does not exist and `ExistsAsync()` returns false
- **THEN** the preflight SHALL throw `PreflightException` with a message indicating the container was not found on the specified account

### Requirement: User-friendly auth error messages
The CLI SHALL translate preflight failures into clear, actionable error messages. The `PreflightException` SHALL carry a user-friendly message (no stack traces). The full exception (including inner exception) SHALL be logged to the audit log.

#### Scenario: Azure CLI not logged in
- **WHEN** no key is found and `AzureCliCredential` throws `CredentialUnavailableException`
- **THEN** the system SHALL display an error message listing all credential sources (--key, ARIUS_KEY, user secrets, az login) and exit with code 1

#### Scenario: Token auth with missing RBAC role
- **WHEN** `AzureCliCredential` authenticates successfully but the preflight receives a 403 Forbidden
- **THEN** the system SHALL display an error message explaining that the RBAC role is missing, specifying `Storage Blob Data Contributor` for archive or `Storage Blob Data Reader` for restore/ls, and include a sample `az role assignment create` command

#### Scenario: Key auth with 403
- **WHEN** `StorageSharedKeyCredential` is used but the preflight receives a 403 Forbidden
- **THEN** the system SHALL display an error message suggesting the account key may be incorrect for the specified account

#### Scenario: Container not found
- **WHEN** the preflight determines the container does not exist (404 or `ExistsAsync()` returns false)
- **THEN** the system SHALL display an error message naming the container and account

### Requirement: Async service provider factory
The `BuildProductionServices` method SHALL be async, returning `Task<IServiceProvider>`. The service provider factory delegate SHALL have the signature `Func<string, string?, string?, string, PreflightMode, Task<IServiceProvider>>` (account name, optional key, optional passphrase, container name, preflight mode). All verb `Build()` methods and `BuildRootCommand` SHALL accept this async delegate signature. Each verb SHALL await the factory call.

#### Scenario: Archive verb passes ReadWrite mode
- **WHEN** the archive verb invokes the service provider factory
- **THEN** it SHALL pass `PreflightMode.ReadWrite`

#### Scenario: Restore verb passes ReadOnly mode
- **WHEN** the restore verb invokes the service provider factory
- **THEN** it SHALL pass `PreflightMode.ReadOnly`

#### Scenario: Ls verb passes ReadOnly mode
- **WHEN** the ls verb invokes the service provider factory
- **THEN** it SHALL pass `PreflightMode.ReadOnly`

#### Scenario: Verb catches PreflightException
- **WHEN** the factory throws `PreflightException`
- **THEN** the verb SHALL catch it, display `[red]Error:[/] {message}` via `AnsiConsole.MarkupLine`, log the full exception, and return exit code 1

### Requirement: Global exception handler
`Program.cs` SHALL wrap the root command invocation in a top-level `try/catch/finally`. Unhandled exceptions SHALL be rendered via `AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything | ExceptionFormats.ShowLinks)` and logged as `Log.Fatal(ex, "Unhandled exception")`. The `finally` block SHALL call `Log.CloseAndFlush()` to ensure the audit log is always written.

#### Scenario: Unhandled exception rendered with Spectre
- **WHEN** an unexpected exception propagates to `Program.cs`
- **THEN** the system SHALL render it via `AnsiConsole.WriteException` with shortened paths and clickable links, and return exit code 1

#### Scenario: Audit log always flushed
- **WHEN** any exception (handled or unhandled) occurs during execution
- **THEN** `Log.CloseAndFlush()` SHALL be called before the process exits

#### Scenario: Known PreflightException does not show stack trace
- **WHEN** a `PreflightException` is caught by a verb
- **THEN** the user SHALL see only the friendly error message, not a stack trace

### Requirement: Key option description
The `--key` / `-k` option description SHALL be `"Azure Storage account key (omit to use Azure CLI login)"`.

#### Scenario: Help text shows updated description
- **WHEN** the user runs `arius --help` or `arius archive --help`
- **THEN** the `--key` option SHALL show description `"Azure Storage account key (omit to use Azure CLI login)"`
