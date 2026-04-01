# Architecture Tests Spec

## Purpose

Defines the architecture test suite that enforces dependency boundaries between Arius projects, ensuring Azure SDK namespaces are only used within `Arius.AzureBlob`.

## Requirements

### Requirement: Only Arius.AzureBlob may depend on Azure SDK namespaces
The architecture tests SHALL enforce that only the `Arius.AzureBlob` assembly may depend on types in Azure SDK namespaces. Specifically, `Arius.Core` and `Arius.Cli` SHALL NOT depend on types in `Azure.Storage`, `Azure.Identity`, `Azure.Core`, or any other `Azure.*` namespace. The existing `Core_Should_Not_Reference_Azure` test SHALL be widened from checking only `Azure.Storage` to checking all `Azure` namespaces. A new `Cli_Should_Not_Reference_Azure` test SHALL be added to enforce the same boundary for the CLI project.

#### Scenario: Core has no Azure dependency
- **WHEN** the architecture tests run against `Arius.Core`
- **THEN** no class in `Arius.Core` SHALL depend on types in any `Azure` namespace (including `Azure.Storage`, `Azure.Identity`, `Azure.Core`)

#### Scenario: CLI has no Azure dependency
- **WHEN** the architecture tests run against `Arius.Cli`
- **THEN** no class in `Arius.Cli` SHALL depend on types in any `Azure` namespace (including `Azure.Storage`, `Azure.Identity`, `Azure.Core`)

#### Scenario: AzureBlob may depend on Azure namespaces
- **WHEN** the architecture tests run against `Arius.AzureBlob`
- **THEN** classes in `Arius.AzureBlob` SHALL be permitted to depend on `Azure.Storage`, `Azure.Identity`, and `Azure.Core` types

#### Scenario: Future Azure dependency in CLI caught by test
- **WHEN** a developer adds a `using Azure.Storage.Blobs` to a file in `Arius.Cli`
- **THEN** the `Cli_Should_Not_Reference_Azure` architecture test SHALL fail
