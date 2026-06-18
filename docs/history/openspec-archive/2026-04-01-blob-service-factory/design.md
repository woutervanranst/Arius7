## Context

`CliBuilder.BuildProductionServices` (lines 147-264 of `CliBuilder.cs`) currently owns the entire chain from credential to validated `IBlobStorageService`: it builds a `BlobServiceClient`, obtains a `BlobContainerClient`, runs a preflight connectivity probe, classifies errors into `PreflightException`, and constructs `AzureBlobStorageService`. The `PreflightMode` enum and `PreflightException` class also live in `Arius.Cli`.

A second (non-CLI) host is planned. Without extraction, that host must duplicate all of this logic. The `Arius.AzureBlob` project is the natural home: it already owns `AzureBlobStorageService` and has the `Azure.Storage.Blobs` dependency. `Arius.Core` must remain Azure-free (enforced by ArchUnit tests).

## Goals / Non-Goals

**Goals:**
- Extract an `AzureBlobServiceFactory` into `Arius.AzureBlob` that any host can call to get a preflight-validated `IBlobStorageService`
- Move `PreflightMode` and `PreflightException` to `Arius.AzureBlob` so both hosts share the same types
- Make `PreflightException` carry structured data (status code, auth mode, account, container) instead of pre-formatted strings, so each host can format its own user-facing messages
- Keep `CliBuilder.BuildProductionServices` as a thin wrapper: resolve credential, call factory, wire DI

**Non-Goals:**
- Changing the credential resolution logic (CLI flag > env var > user secrets > `AzureCliCredential`) -- that stays in CLI
- Adding a second host in this change -- that is a separate future change
- Modifying `Arius.Core` or `IBlobStorageService` in any way
- Adding a new test project for `Arius.AzureBlob` -- the factory is tested transitively through existing CLI tests

## Decisions

### 1. Factory lives in `Arius.AzureBlob` as a static async method

The factory is a static `CreateAsync` method on a new `AzureBlobServiceFactory` class. It takes a `TokenCredential` or `StorageSharedKeyCredential` (via the common base -- both are passed as typed overloads or a discriminated parameter), account name, container name, and `PreflightMode`. It returns an `IBlobStorageService`.

**Why static**: The factory has no instance state. It builds the client chain, runs the preflight probe, and returns the service. No reason to instantiate it.

**Why `Arius.AzureBlob`**: This project already depends on `Azure.Storage.Blobs` and owns `AzureBlobStorageService`. The ArchUnit rules forbid Core from having Azure dependencies, so the factory cannot go there. Putting it in a new shared project would add unnecessary complexity for a single class.

**Alternative considered**: An instance-based factory registered in DI. Rejected because the factory must run *before* the DI container is built (preflight must pass before services are registered), so DI registration is circular.

### 2. Credential parameter as `object` with runtime type check

The caller passes the credential as `object` -- either a `StorageSharedKeyCredential` or a `TokenCredential`. The factory uses pattern matching to construct the appropriate `BlobServiceClient` overload.

**Why `object`**: `StorageSharedKeyCredential` and `TokenCredential` share no common base class in the Azure SDK. A generic parameter or discriminated union would add complexity for no benefit since there are exactly two cases. The factory also needs to know which kind was used (for the `AuthMode` field on `PreflightException`), so it inspects the type anyway.

**Alternative considered**: Two separate factory methods (`CreateWithKeyAsync`, `CreateWithTokenAsync`). Rejected because most of the logic (container client construction, preflight probe, error classification) is identical -- two methods would mean duplication inside the factory.

### 3. `PreflightException` carries structured fields, not formatted messages

`PreflightException` changes from `PreflightException(string userMessage, Exception? inner)` to a class with typed properties:

- `int? StatusCode` -- HTTP status code from `RequestFailedException`, or null for `CredentialUnavailableException`
- `string AuthMode` -- `"key"` or `"token"`, so the host knows which remediation to suggest
- `string AccountName`
- `string ContainerName`
- `PreflightErrorKind ErrorKind` -- enum: `ContainerNotFound`, `AccessDenied`, `CredentialUnavailable`, `Other`
- `Exception? InnerException` -- the original SDK exception

**Why structured**: The CLI formats errors with Spectre.Console markup and specific remediation text (e.g., "az role assignment create ..."). A web host would format differently. Pre-formatted strings force all hosts to accept CLI-specific messages.

**Why `ErrorKind` enum**: Prevents each host from re-inspecting the inner exception status codes. The factory already classifies errors; the enum captures that classification.

### 4. Add `Azure.Identity` to `Arius.AzureBlob`

The factory catches `CredentialUnavailableException` (from `Azure.Identity`) to classify token auth failures. This requires adding `Azure.Identity` as a package reference to `Arius.AzureBlob.csproj`.

**Why acceptable**: `Arius.AzureBlob` is already the Azure-specific layer. Adding `Azure.Identity` keeps identity concerns in the same boundary. The ArchUnit rules only forbid Azure dependencies in Core, not in AzureBlob.

### 5. Architecture test: only `Arius.AzureBlob` may depend on Azure namespaces

Add a new ArchUnit test `Cli_Should_Not_Reference_Azure` that asserts `Arius.Cli` classes do not depend on types in the `Azure.*` namespaces (`Azure.Storage`, `Azure.Identity`, `Azure.Core`, etc.). This replaces the current narrow `Core_Should_Not_Reference_Azure` (which only checks `Azure.Storage`) with a broader rule: both Core and CLI must be free of Azure types, leaving `Arius.AzureBlob` as the sole project that may reference them.

The existing `Core_Should_Not_Reference_Azure` test is also widened from `Azure.Storage` to `Azure` (all Azure namespaces) for consistency.

**Why**: After extracting the factory, `Arius.Cli` should no longer reference Azure SDK types directly -- it passes credentials and receives an `IBlobStorageService`. The architecture test ensures this boundary is not accidentally violated in the future. This codifies the design intent: Azure-specific concerns live exclusively in `Arius.AzureBlob`.

**What passes through**: `Arius.Cli` will still depend on `Arius.AzureBlob` (to call `AzureBlobServiceFactory.CreateAsync` and catch `PreflightException`). The test checks direct Azure SDK namespace dependencies, not transitive project references.

### 6. CLI error formatting moves to verb catch blocks

Currently the verbs catch `PreflightException` and display `ex.Message`. After the change, they will switch on `ex.ErrorKind` and format host-specific messages using `ex.AccountName`, `ex.ContainerName`, `ex.AuthMode`, and `ex.StatusCode`.

**Why verb-level**: The verbs already have the catch blocks. Centralizing formatting in a single CLI helper method is fine too, but the verbs already differentiate on `PreflightMode` for the RBAC role suggestion ("Contributor" for archive, "Reader" for restore/ls), so they need per-verb logic anyway.

## Risks / Trade-offs

- **Breaking change for test helpers**: `Arius.Cli.Tests` uses a factory delegate typed `Func<string, string?, string?, string, PreflightMode, Task<IServiceProvider>>`. The `PreflightMode` namespace changes from `Arius.Cli` to `Arius.AzureBlob`, requiring a `using` update. No behavioral change. **Mitigation**: Straightforward find-and-replace.

- **`Azure.Identity` transitive dependency size**: Adding `Azure.Identity` to `AzureBlob` pulls in MSAL and related packages at the library level. However, `Arius.Cli` already depends on `Azure.Identity`, and AzureBlob is only consumed by CLI (and the future second host, which will also need it). **Mitigation**: No actual binary size increase since the dependency is already transitively present.

- **Enum evolution**: Adding a new `PreflightErrorKind` value in the future requires updating all host switch blocks. **Mitigation**: There are only two hosts planned, and the enum is small (4 values). Exhaustive switch with a default throw will catch missing cases at compile time.
