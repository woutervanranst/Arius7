# Shared Test Infrastructure Design

## Goal

Extract reusable Docker-backed and repository-fixture test infrastructure into a dedicated non-test library so `Arius.E2E.Tests` no longer depends on `Arius.Integration.Tests`.

## Problem

`src/Arius.E2E.Tests/Arius.E2E.Tests.csproj` currently references `src/Arius.Integration.Tests/Arius.Integration.Tests.csproj` only to reuse `AzuriteFixture`. That creates a structural problem:

- a test project depends on another test project
- CI project discovery has to infer indirect Docker requirements
- `dotnet test` selection becomes fragile because reusable test infrastructure lives inside a test assembly

The temporary CI workaround in `.github/scripts/Get-DotNetProjectMatrix.ps1` fixes the immediate macOS failure, but the dependency shape is still wrong.

## Design

Create a new non-test library at `src/Arius.Tests.Shared/` and move genuinely reusable test infrastructure there.

### Shared library contents

Move these into `Arius.Tests.Shared`:

- `AzuriteFixture`
- a new shared repository fixture base extracted from the duplicated setup in `PipelineFixture` and `E2EFixture`

The shared repository fixture base should own:

- temp root / source root / restore root creation
- encryption selection
- `ChunkIndexService`, `ChunkStorageService`, `FileTreeService`, and `SnapshotService` construction
- `ArchiveCommandHandler` and `RestoreCommandHandler` creation
- basic file helpers like write/read/exists in source and restore roots

It should accept an already-created `IBlobContainerService`, account name, and container name so the same base can work with Azurite and live Azure.

### Project-specific wrappers

Keep thin project-specific wrappers:

- `PipelineFixture` in `Arius.Integration.Tests`
- `E2EFixture` in `Arius.E2E.Tests`

Those wrappers may keep project-specific behavior:

- `PipelineFixture`: integration-test convenience APIs such as list-query handler creation and existing-container reuse helpers
- `E2EFixture`: repository-cache preservation and cleanup lifecycle that is specific to E2E cold/warm scenarios

### What stays out of shared

Do not move scenario-specific or project-specific helpers:

- deterministic dataset generator and scenario runner code in `Arius.E2E.Tests`
- archive-tier verification helpers such as `CopyTrackingBlobService`
- integration-only pipeline fakes such as `RehydrationSimulatingBlobService`, `FaultingBlobService`, and `CbcEncryptionServiceAdapter`

## Expected outcome

After the refactor:

- `Arius.E2E.Tests` references `Arius.Tests.Shared`, not `Arius.Integration.Tests`
- Docker-backed Azurite infrastructure is reusable without living in a test assembly
- the CI discovery workaround can be reverted because the structural dependency is gone

## Verification

The refactor is complete when:

- `src/Arius.E2E.Tests/Arius.E2E.Tests.csproj` has no project reference to `Arius.Integration.Tests`
- `src/Arius.Integration.Tests/Arius.Integration.Tests.csproj` and `src/Arius.E2E.Tests/Arius.E2E.Tests.csproj` both reference `Arius.Tests.Shared`
- the CI project discovery script no longer needs to special-case `Arius.Integration.Tests.csproj` references
- focused E2E, integration, and CI-discovery verification passes
