## ADDED Requirements

### Requirement: AzureBlobServiceFactory creates preflight-validated storage service
The `Arius.AzureBlob` project SHALL expose a static `AzureBlobServiceFactory.CreateAsync` method that accepts a credential (`StorageSharedKeyCredential` or `TokenCredential` passed as `object`), account name, container name, and `PreflightMode`, and SHALL return an `IBlobStorageService` that has passed the preflight connectivity check. The factory SHALL construct a `BlobServiceClient`, obtain a `BlobContainerClient`, execute the preflight probe, and wrap the container client in an `AzureBlobStorageService`.

#### Scenario: Create with shared key credential
- **WHEN** `AzureBlobServiceFactory.CreateAsync` is called with a `StorageSharedKeyCredential`, account `"myaccount"`, container `"backup"`, and `PreflightMode.ReadOnly`
- **THEN** the factory SHALL return an `IBlobStorageService` backed by a `BlobContainerClient` authenticated with the shared key

#### Scenario: Create with token credential
- **WHEN** `AzureBlobServiceFactory.CreateAsync` is called with an `AzureCliCredential` (a `TokenCredential`), account `"myaccount"`, container `"backup"`, and `PreflightMode.ReadWrite`
- **THEN** the factory SHALL return an `IBlobStorageService` backed by a `BlobContainerClient` authenticated with the token credential

#### Scenario: Invalid credential type rejected
- **WHEN** `AzureBlobServiceFactory.CreateAsync` is called with a credential that is neither `StorageSharedKeyCredential` nor `TokenCredential`
- **THEN** the factory SHALL throw `ArgumentException`

### Requirement: Preflight probe modes
The factory SHALL execute a preflight connectivity check based on the `PreflightMode` parameter. `PreflightMode.ReadWrite` SHALL upload and delete a probe blob named `.arius-preflight-probe`. `PreflightMode.ReadOnly` SHALL call `container.ExistsAsync()` and throw `PreflightException` with `ErrorKind.ContainerNotFound` if the container does not exist. The `PreflightMode` enum SHALL be defined in `Arius.AzureBlob`.

#### Scenario: ReadWrite preflight succeeds
- **WHEN** the factory runs with `PreflightMode.ReadWrite` and the credential has write access
- **THEN** the factory SHALL upload an empty blob to `.arius-preflight-probe`, delete it, and return the service

#### Scenario: ReadOnly preflight succeeds
- **WHEN** the factory runs with `PreflightMode.ReadOnly` and the container exists
- **THEN** the factory SHALL verify the container exists and return the service

#### Scenario: ReadOnly preflight container not found
- **WHEN** the factory runs with `PreflightMode.ReadOnly` and the container does not exist
- **THEN** the factory SHALL throw `PreflightException` with `ErrorKind` set to `ContainerNotFound`

### Requirement: Error classification into PreflightException
The factory SHALL catch Azure SDK exceptions during the preflight check and classify them into a `PreflightException` with structured fields. The `PreflightException` class SHALL be defined in `Arius.AzureBlob` and SHALL carry the following properties: `PreflightErrorKind ErrorKind`, `int? StatusCode`, `string AuthMode` (`"key"` or `"token"`), `string AccountName`, `string ContainerName`. The `PreflightException` SHALL NOT carry pre-formatted user-facing messages (the `Message` property MAY contain a generic description but hosts SHALL use the structured fields for formatting).

The `PreflightErrorKind` enum SHALL define: `ContainerNotFound`, `AccessDenied`, `CredentialUnavailable`, `Other`.

#### Scenario: 404 RequestFailedException classified as ContainerNotFound
- **WHEN** the preflight probe throws `RequestFailedException` with status 404
- **THEN** the factory SHALL throw `PreflightException` with `ErrorKind = ContainerNotFound`, `StatusCode = 404`, and the original exception as `InnerException`

#### Scenario: 403 RequestFailedException classified as AccessDenied
- **WHEN** the preflight probe throws `RequestFailedException` with status 403
- **THEN** the factory SHALL throw `PreflightException` with `ErrorKind = AccessDenied`, `StatusCode = 403`, `AuthMode` reflecting the credential type used, and the original exception as `InnerException`

#### Scenario: CredentialUnavailableException classified as CredentialUnavailable
- **WHEN** the preflight probe throws `CredentialUnavailableException`
- **THEN** the factory SHALL throw `PreflightException` with `ErrorKind = CredentialUnavailable`, `StatusCode = null`, and the original exception as `InnerException`

#### Scenario: Other RequestFailedException classified as Other
- **WHEN** the preflight probe throws `RequestFailedException` with a status code other than 403 or 404
- **THEN** the factory SHALL throw `PreflightException` with `ErrorKind = Other` and the original exception as `InnerException`

#### Scenario: AuthMode reflects key credential
- **WHEN** a `StorageSharedKeyCredential` is passed and a 403 error occurs
- **THEN** the `PreflightException.AuthMode` SHALL be `"key"`

#### Scenario: AuthMode reflects token credential
- **WHEN** a `TokenCredential` is passed and a 403 error occurs
- **THEN** the `PreflightException.AuthMode` SHALL be `"token"`

### Requirement: Azure.Identity dependency in Arius.AzureBlob
The `Arius.AzureBlob` project SHALL reference the `Azure.Identity` package to enable catching `CredentialUnavailableException` in the factory. This dependency SHALL be added to `Arius.AzureBlob.csproj` and the version SHALL be managed via `Directory.Packages.props`.

#### Scenario: CredentialUnavailableException is catchable
- **WHEN** the factory code catches `CredentialUnavailableException`
- **THEN** the code SHALL compile without errors because `Azure.Identity` is referenced
