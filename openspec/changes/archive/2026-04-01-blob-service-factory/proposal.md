## Why

The `BlobServiceClient` → `BlobContainerClient` → `AzureBlobStorageService` construction and preflight connectivity check currently live inside `Arius.Cli.CliBuilder`. A second host (non-CLI) is planned. Without extraction, that host would duplicate the client construction, preflight probe logic, error classification (403/404/credential-unavailable), and `PreflightException` type. Moving the shareable parts into `Arius.AzureBlob` eliminates this duplication while keeping host-specific concerns (credential selection, key resolution, error message formatting) in each host.

## What Changes

- Extract a new `BlobServiceFactory` class into `Arius.AzureBlob` that encapsulates:
  - `BlobServiceClient` → `BlobContainerClient` → `AzureBlobStorageService` construction from a caller-supplied credential
  - Preflight probe logic (`ReadWrite`: upload/delete `.arius-preflight-probe`; `ReadOnly`: `ExistsAsync`)
  - Error classification: catches `RequestFailedException` (403, 404) and `CredentialUnavailableException`, wraps them in a `PreflightException` with structured data (status code, auth mode, account name, container name) rather than formatted user-facing strings
- Move `PreflightMode` enum and `PreflightException` class from `Arius.Cli` to `Arius.AzureBlob`
- `PreflightException` changes from carrying a pre-formatted user-facing message to carrying structured fields (status code, account, container, auth mode) — each host formats its own error messages
- Add `Azure.Identity` package reference to `Arius.AzureBlob` (needed for `CredentialUnavailableException` catch)
- Slim down `CliBuilder.BuildProductionServices` to: resolve credential → call `BlobServiceFactory.CreateAsync(...)` → build DI container
- CLI verbs continue to catch `PreflightException` and format host-specific error messages using Spectre.Console markup

## Capabilities

### New Capabilities
- `blob-service-factory`: Factory that constructs an authenticated, preflight-validated `IBlobStorageService` from a caller-supplied credential, account name, container name, and preflight mode

### Modified Capabilities
- `cli`: `CliBuilder.BuildProductionServices` delegates client construction and preflight to `BlobServiceFactory`; `PreflightMode` and `PreflightException` imports change from `Arius.Cli` to `Arius.AzureBlob`; error message formatting moves from `PreflightException.Message` to verb-level formatting of structured exception fields; all direct Azure SDK usings (`Azure.Identity`, `Azure.Storage`, `Azure.Core`) are removed from CLI code
- `architecture-tests`: Add `Cli_Should_Not_Reference_Azure` test enforcing that only `Arius.AzureBlob` may depend on Azure SDK namespaces; widen existing `Core_Should_Not_Reference_Azure` from `Azure.Storage` to all `Azure` namespaces

## Impact

- **Arius.AzureBlob** — new `BlobServiceFactory` class, new `Azure.Identity` dependency, `PreflightMode` enum and `PreflightException` class move here
- **Arius.Cli** — `CliBuilder.cs` shrinks significantly; verbs update `PreflightException` catch to format structured fields; `using` statements change namespace
- **Arius.Core** — no changes (architecture boundary preserved)
- **Tests** — `Arius.Cli.Tests` catch block assertions may need updating for structured `PreflightException`; new ArchUnit test enforcing Azure dependency boundary; no new test project needed (factory is tested transitively through CLI tests and future host tests)
