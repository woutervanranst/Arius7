---
status: accepted
date: 2026-04-24
decision-makers: Wouter Van Ranst, OpenCode
---

# Structure Representative End-to-End Coverage Around Shared Infrastructure And One Canonical Workflow

## Context and Problem Statement

This PR evolved through several superpowers specs and plans while refactoring representative end-to-end coverage. The initial direction used isolated scenario runs and reused Azurite infrastructure through a test-project-to-test-project dependency, which made the representative suite structurally awkward and weakened its ability to validate one realistic archive history over time.

The implemented outcome needed to solve two linked problems at once: reusable Docker-backed test infrastructure had to move out of test assemblies, and representative E2E coverage had to validate one evolving repository history across Azurite and Azure rather than a matrix of disconnected one-off scenarios.

## Decision Drivers

* representative coverage should validate one realistic archive history rather than disconnected scenario setup
* Azurite and Azure should share the same representative story wherever backend capabilities allow it
* representative test data must stay deterministic and be easy to tune for runtime cost
* archive-tier behavior must stay real and capability-gated rather than being faked for Azurite
* representative assertions should prefer stable snapshot, deduplication, and restore invariants over brittle exact blob-layout counts

## Considered Options

* Keep the isolated representative scenario matrix and direct `Arius.E2E.Tests -> Arius.Integration.Tests` dependency
* Move shared fixtures into a non-test library but keep the isolated representative scenario matrix
* Move shared fixtures into a non-test library and run one canonical representative workflow per backend

## Decision Outcome

Chosen option: "Move shared fixtures into a non-test library and run one canonical representative workflow per backend", because it removes the structural test-project coupling and makes the representative suite exercise one deterministic archive history across Azurite and Azure with capability-gated archive-tier behavior.

### Consequences

* Good, because `Arius.Tests.Shared` now owns reusable Azurite and repository-fixture wiring that both `Arius.Integration.Tests` and `Arius.E2E.Tests` consume.
* Good, because representative coverage now models one evolving `V1 -> V2` history including warm restore, cold restore, previous-version restore, no-op re-archive, `--no-pointers`, `--remove-local`, conflict behavior, and archive-tier pending-versus-ready behavior.
* Good, because the representative dataset remains deterministic and its runtime cost is controlled by one explicit size constant in `SyntheticRepositoryDefinitionFactory`.
* Good, because representative assertions now include remote-state checks such as snapshot lineage and deduplication behavior without coupling the suite to brittle exact chunk or filetree totals.
* Good, because archive-tier coverage was simplified to the essential two-pass lifecycle: pending restore with staged rehydration blobs, then ready restore with cleanup.
* Bad, because the canonical workflow is broader than a single isolated scenario, so failures require reading step boundaries carefully.
* Bad, because the final code shape does not preserve every intermediate plan idea; the code and this ADR are the authoritative end state.

### Confirmation

* `src/Arius.Tests.Shared/` contains shared Azurite and repository-fixture infrastructure, including `Storage/AzuriteFixture.cs` and `Fixtures/RepositoryTestFixture.cs`.
* `src/Arius.E2E.Tests/Arius.E2E.Tests.csproj` and `src/Arius.Integration.Tests/Arius.Integration.Tests.csproj` both reference `Arius.Tests.Shared`, and `Arius.E2E.Tests` no longer references `Arius.Integration.Tests`.
* `src/Arius.E2E.Tests/RepresentativeArchiveRestoreTests.cs` runs `RepresentativeWorkflowCatalog.Canonical` on Azurite and Azure, with live Azure remaining credential-gated.
* `src/Arius.E2E.Tests/Workflows/` contains the canonical workflow definition, runner, state, result, and typed workflow steps used by representative E2E coverage.
* `src/Arius.E2E.Tests/Datasets/SyntheticRepositoryDefinitionFactory.cs` contains the explicit representative dataset scale control.
* `.github/scripts/Get-DotNetProjectMatrix.ps1` no longer needs a special case for an `Arius.E2E.Tests -> Arius.Integration.Tests` relationship.

## Pros and Cons of the Options

### Keep the isolated representative scenario matrix and direct `Arius.E2E.Tests -> Arius.Integration.Tests` dependency

This keeps the original structure: many isolated representative scenarios with E2E reusing Azurite infrastructure from another test assembly.

* Good, because it minimizes short-term refactoring.
* Good, because isolated scenarios can be simpler to reason about individually.
* Bad, because it preserves the test-project coupling and the CI/project-discovery problems that came with it.
* Bad, because it keeps representative coverage focused on disconnected setup states instead of one evolving repository history.

### Move shared fixtures into a non-test library but keep the isolated representative scenario matrix

This fixes the project-graph problem but keeps representative coverage modeled as separate scenarios.

* Good, because it removes the structural `E2E -> Integration` dependency.
* Good, because it is less disruptive than rewriting representative orchestration.
* Neutral, because it still preserves most existing scenario-based test code.
* Bad, because it still under-tests the main representative story: one repository changing over time.
* Bad, because cold/warm cache and version progression remain modeled as disconnected preconditions instead of workflow transitions.

### Move shared fixtures into a non-test library and run one canonical representative workflow per backend

This is the implemented design.

* Good, because it fixes both the structural dependency problem and the representative-history modeling problem in one design.
* Good, because it lets Azurite and Azure share the same representative workflow while still keeping archive-tier behavior capability-gated.
* Good, because it gives the suite one clear, deterministic story that matches the main archive and restore lifecycle.
* Bad, because the canonical workflow is a larger test surface and can be slower or noisier to debug than a tiny isolated scenario.

## More Information

This ADR captures the implemented outcome of the PR after several iterations recorded in:

* `docs/superpowers/specs/2026-04-20-shared-test-infrastructure-design.md`
* `docs/superpowers/specs/2026-04-23-representative-workflow-design.md`

The intermediate implementation plans under `docs/superpowers/plans/` were exploratory and were superseded by the final code and this ADR, so they were removed as part of this cleanup.
