## 1. Define types in Arius.AzureBlob

- [x] 1.1 Add `Azure.Identity` package reference to `Arius.AzureBlob.csproj`
- [x] 1.2 Create `PreflightErrorKind` enum (`ContainerNotFound`, `AccessDenied`, `CredentialUnavailable`, `Other`) in `Arius.AzureBlob`
- [x] 1.3 Create `PreflightMode` enum (`ReadOnly`, `ReadWrite`) in `Arius.AzureBlob`
- [x] 1.4 Create `PreflightException` class in `Arius.AzureBlob` with structured fields (`ErrorKind`, `StatusCode`, `AuthMode`, `AccountName`, `ContainerName`) and inner exception

## 2. Implement AzureBlobServiceFactory

- [x] 2.1 Create `AzureBlobServiceFactory` class in `Arius.AzureBlob` with static `CreateAsync` method accepting `object` credential, account name, container name, and `PreflightMode`
- [x] 2.2 Implement credential type check via pattern matching (`StorageSharedKeyCredential` / `TokenCredential`), throw `ArgumentException` for invalid types
- [x] 2.3 Implement `BlobServiceClient` and `BlobContainerClient` construction
- [x] 2.4 Implement preflight probe: `ReadWrite` uploads/deletes `.arius-preflight-probe` blob, `ReadOnly` calls `ExistsAsync` and throws on missing container
- [x] 2.5 Implement error classification: catch `RequestFailedException` (403 → `AccessDenied`, 404 → `ContainerNotFound`, other → `Other`) and `CredentialUnavailableException` (→ `CredentialUnavailable`), wrap in structured `PreflightException`
- [x] 2.6 Return `IBlobStorageService` (as `AzureBlobStorageService` wrapping the container client) on success

## 3. Refactor CLI to use factory

- [x] 3.1 Remove `PreflightMode` enum and `PreflightException` class from `CliBuilder.cs`
- [x] 3.2 Slim `BuildProductionServices` to: resolve credential, call `AzureBlobServiceFactory.CreateAsync`, build DI container with returned `IBlobStorageService`
- [x] 3.3 Remove direct Azure SDK `using` statements from `CliBuilder.cs` (`Azure`, `Azure.Core`, `Azure.Identity`, `Azure.Storage`, `Azure.Storage.Blobs`, `Azure.Storage.Blobs.Models`)
- [x] 3.4 Update verb catch blocks (Archive, Restore, Ls) to switch on `PreflightException.ErrorKind` and format host-specific error messages using structured fields
- [x] 3.5 Remove `Azure.Identity` package reference from `Arius.Cli.csproj`
- [x] 3.6 Update `using` statements across CLI files to reference `Arius.AzureBlob` namespace for `PreflightMode` and `PreflightException`

## 4. Architecture tests

- [x] 4.1 Widen `Core_Should_Not_Reference_Azure` from `Azure.Storage` to `Azure` (all Azure namespaces)
- [x] 4.2 Add `Cli_Should_Not_Reference_Azure` test enforcing CLI has no `Azure` namespace dependencies

## 5. Verify

- [x] 5.1 Run architecture tests (`dotnet test --project src/Arius.Architecture.Tests`)
- [x] 5.2 Run CLI tests (`dotnet test --project src/Arius.Cli.Tests`)
- [x] 5.3 Run Core tests (`dotnet test --project src/Arius.Core.Tests`)
- [x] 5.4 Run full build to confirm no compilation errors (`dotnet build src/Arius.Cli`)
