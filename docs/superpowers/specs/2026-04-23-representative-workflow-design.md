# Representative Workflow Design

## Goal

Refactor the representative end-to-end suite from isolated one-off scenarios into one canonical workflow that exercises one evolving repository history inside a single backend container and a single local fixture lineage.

The same canonical workflow should run against Azurite and Azure. Azure-only archive-tier semantics stay inside the same workflow behind capability-gated steps rather than separate top-level workflows.

This design also keeps the workflow benchmark-ready without introducing benchmark code yet.

## Additional Constraints

- remove representative-suite code that becomes obsolete as part of the refactor rather than carrying both models in parallel
- this test-suite refactor does not need a strict red-green-refactor or TDD workflow
- introduce one explicit constant that controls the size of the representative synthetic repository so development can run against a smaller profile without redesigning the workflow
- for the current development pass, reduce the representative dataset target to roughly 30 MB and roughly 300 files, while keeping the structure easy to tune upward later

## Current Problem

The current representative suite models each scenario as an isolated run:

- each scenario gets a fresh backend context and a fresh blob container
- each scenario gets a fresh temp root on disk
- setup history is synthesized independently for each scenario
- `Warm` and `Cold` cache states are mostly treated as scenario preconditions rather than transitions within one evolving repository history

That structure validates many behaviors, but it does not validate the main property the representative suite was intended to cover: one repository archive history with iterative operations applied over time.

## Desired Outcome

The representative suite should model one realistic repository lifecycle:

1. materialize `V1`
2. archive `V1`
3. restore and verify `V1`
4. materialize deterministic `V2` changes in the same source root
5. archive again into the same container
6. restore latest and verify `V2`
7. restore previous and verify `V1`
8. exercise warm-cache and cold-cache restore behavior against the same remote history
9. exercise no-op re-archive against the same remote history
10. optionally exercise `--no-pointers` and `--remove-local` subflows inside the same canonical workflow
11. if supported by the backend, exercise archive-tier pending vs ready restore behavior and rehydrated chunk cleanup

## Proposed Structure

### Canonical Workflow Definition

Replace the current `RepresentativeScenarioDefinition` matrix with one `RepresentativeWorkflowDefinition` that owns an ordered list of typed steps.

The workflow definition should be explicit and small. It should describe one canonical representative repository lifecycle, not a mini language for arbitrary future workflows.

Suggested shape:

```csharp
internal sealed record RepresentativeWorkflowDefinition(
    string Name,
    SyntheticRepositoryProfile Profile,
    int Seed,
    IReadOnlyList<IRepresentativeWorkflowStep> Steps);
```

The initial version should only define one workflow instance, for example `RepresentativeWorkflowCatalog.Canonical`.

The workflow definition should stay independent from dataset scale. Dataset scale should be controlled by a separate constant in the synthetic repository definition factory or profile builder so the same workflow can run against a development-sized representative repository now and a larger representative repository later.

### Workflow Runner

Add a `RepresentativeWorkflowRunner` that:

- creates one backend context for the whole workflow run
- creates one fixture for the whole workflow run
- executes each typed step in order
- stores workflow state between steps
- exposes step boundaries clearly in failures and logs

This runner replaces the current `RepresentativeScenarioRunner` as the primary representative E2E orchestration entry point.

### Workflow State

The runner should maintain explicit state instead of recomputing scenario preconditions. Suggested state:

```csharp
internal sealed class RepresentativeWorkflowState
{
    public required E2EStorageBackendContext Context { get; init; }
    public required E2EFixture Fixture { get; init; }
    public required SyntheticRepositoryDefinition Definition { get; init; }
    public required int Seed { get; init; }

    public SyntheticRepositoryVersion? CurrentSourceVersion { get; set; }
    public string? PreviousSnapshotVersion { get; set; }
    public string? LatestSnapshotVersion { get; set; }
    public RepositoryTreeSnapshot? CurrentMaterializedSnapshot { get; set; }

    public int SnapshotCount { get; set; }
    public int ChunkBlobCount { get; set; }
    public int FileTreeBlobCount { get; set; }
}
```

The exact fields can vary, but the state must carry enough information to support assertions about:

- snapshot lineage
- expected dataset version
- warm vs cold cache transitions
- remote blob counts before and after selected operations

## Step Model

Keep the step model intentionally small and explicit.

Suggested step types:

- `MaterializeVersionStep`
- `ArchiveStep`
- `RestoreStep`
- `ResetCacheStep`
- `AssertRemoteStateStep`
- `AssertConflictBehaviorStep`
- `ArchiveTierLifecycleStep`

Avoid a generic instruction DSL. Each step type should correspond to a concrete test concern that already exists in the representative suite.

### Step Responsibilities

#### MaterializeVersionStep

Writes the requested synthetic dataset version into the current fixture source root and records the expected snapshot tree for later assertions.

Use cases:

- initial `V1` materialization
- deterministic `V2` mutation application into the same logical repository history

#### ArchiveStep

Runs archive with explicit options and records the returned snapshot timestamp/version for later restore steps.

When the archive result returns the same snapshot version already recorded as latest, the step must treat the archive as a no-op and leave `PreviousSnapshotVersion` and `LatestSnapshotVersion` unchanged. This keeps workflow state aligned with the product rule that unchanged archive runs preserve the existing latest snapshot instead of publishing a redundant snapshot.

Configurable flags should be limited to current known needs:

- upload tier
- `NoPointers`
- `RemoveLocal`

This step is where optional typed substeps for `--no-pointers` and `--remove-local` are expressed.

#### RestoreStep

Runs restore and verifies the restored tree against either the current or previous expected dataset version. It should support:

- latest version restore
- previous version restore
- warm-cache restore
- cold-cache restore
- overwrite on/off
- optional target path when archive-tier subtree restore is exercised

#### ResetCacheStep

Deletes the repository cache for the current account/container so cold-cache restores become explicit transitions within the same workflow.

#### AssertRemoteStateStep

Asserts stable repository/container invariants after a step boundary. This is how the canonical workflow checks more than just local restore results.

#### AssertConflictBehaviorStep

Prepares local conflicting files and verifies overwrite or no-overwrite restore behavior. Keeping it separate avoids overloading the generic restore step with conflict setup responsibilities.

#### ArchiveTierLifecycleStep

Encapsulates the Azure-only archive-tier lifecycle:

- archive selected content to Archive tier
- assert rehydration planning is offered
- assert pending restore behavior
- assert that pending restore created one or more blobs under `chunks-rehydrated/`
- assert rerun does not issue duplicate copy work while still pending
- delete the pending `chunks-rehydrated/` blobs created by the first restore attempt
- sideload ready rehydrated chunks under `chunks-rehydrated/` with a helper that recreates the rehydrated tar content deterministically
- restore successfully once ready
- assert cleanup of rehydrated blobs

This step should self-skip when backend capabilities do not support archive-tier semantics.

## Canonical Workflow Contents

The canonical workflow should cover the following in one run:

1. materialize `V1`
2. archive `V1` to `Cool`
3. assert initial remote state
4. restore latest and verify `V1`
5. materialize `V2`
6. archive `V2` to `Cool`
7. assert incremental remote state
8. restore latest with warm cache and verify `V2`
9. reset local cache
10. restore latest with cold cache and verify `V2`
11. restore previous and verify `V1`
12. archive `V2` again with no local changes
13. assert no-op archive invariants
14. run `--no-pointers` archive substep and verify restore behavior accordingly
15. run `--remove-local` archive substep followed by restore verification
16. if `SupportsArchiveTier`, run archive-tier lifecycle assertions including simulated ready rehydration

This does not need to mean a single giant test method with ad hoc branching. The workflow remains one definition executed by typed step executors.

## Remote Assertions

The canonical workflow should assert stable repository/container properties in addition to end-to-end disk behavior.

### Safe Cross-Backend Assertions

These are stable enough for both Azurite and Azure.

#### Snapshot creation

After each successful state-changing archive, snapshot count increases by one. No-op archive runs are the explicit exception: if the rebuilt filetree root is content-equivalent to the latest snapshot, Arius returns the existing latest snapshot timestamp/root hash and does not create another snapshot manifest.

Observation options:

- list blobs under `snapshots/`
- or query through `SnapshotService`

#### No-op archive snapshot lineage

After a no-change re-archive:

- snapshot count remains unchanged
- the latest snapshot version remains the same as before the no-op archive
- the archive result points at that preserved snapshot timestamp/root hash
- latest and previous snapshots still represent the two most recent distinct repository states, not the no-op command invocation

This validates that Arius treats snapshots as repository state changes rather than command-invocation history.

#### Snapshot totals

Latest snapshot `FileCount` and `TotalSize` match the expected synthetic dataset version being archived.

#### No-op archive storage stability

After the no-change re-archive:

- `snapshots/` blob count does not increase
- `chunks/` blob count does not increase
- `filetrees/` blob count does not increase

Do not assert exact total counts. Exact counts are too coupled to bundling implementation details.

#### Deduplication lookup

For known duplicate binary content in the deterministic dataset:

- multiple paths share the same content hash
- `ChunkIndexService.LookupAsync(contentHash)` resolves successfully
- adding a second path with the same content does not create a second unique chunk for that content

The test should prefer chunk-index and content-hash based assertions over raw blob naming assumptions.

#### Small-file tar path

For a known small file in the dataset:

- the content hash resolves through the chunk index
- the resolved chunk hash differs from the content hash

This validates that the file went through the tar-backed path rather than becoming a direct large chunk.

#### Pointer-file expectations

Restore verification should assert pointer file presence for normal archive steps and pointer file absence for `--no-pointers` substeps.

### Azure-Only Assertions

These stay inside archive-tier capability-gated steps.

#### Archive-tier planning

- `ConfirmRehydration` is invoked
- the estimate reports chunks needing or pending rehydration

#### Pending restore behavior

- initial archive-tier restore returns success with pending chunks
- no files are restored while required chunks are not yet ready

#### Rerun while pending

- rerunning restore while chunks are still pending does not trigger duplicate copy operations

#### Ready restore and cleanup

- initial pending restore creates one or more blobs under `chunks-rehydrated/`
- the test deletes those pending blobs before sideloading deterministic ready blobs
- restore succeeds once `chunks-rehydrated/` contains the ready chunk copy
- rehydrated chunk cleanup is offered and executed
- `chunks-rehydrated/` is cleaned up after the ready restore path

## Capability Gating

The workflow definition itself remains shared. Capability differences are handled only inside step execution.

Rules:

- Azurite and Azure both run the same canonical workflow definition
- archive-tier lifecycle steps self-skip or no-op when `SupportsArchiveTier` is false
- non-archive representative behavior must remain identical across both backends
- no backend-specific fork of the main workflow should be introduced

This preserves one representative story while still honoring real Azure-only semantics.

## Benchmark Readiness

The workflow runner should be structured so that future benchmarks can measure either the whole workflow or selected step boundaries without redesigning the suite.

The runner should therefore expose step boundaries and stable step names. It does not need to include benchmark code now.

Recommended readiness hooks:

- each step has a stable name
- runner emits start/end events or captures timestamps per step
- setup data and measured operation boundaries remain explicit
- workflow definition is immutable and deterministic

This makes it straightforward later to benchmark:

- full canonical workflow
- second archive only
- latest restore with warm cache
- latest restore with cold cache
- archive-tier ready restore path

## File-Level Changes

### Replace current representative scenario model

Likely remove or supersede:

- `src/Arius.E2E.Tests/Scenarios/RepresentativeScenarioDefinition.cs`
- `src/Arius.E2E.Tests/Scenarios/RepresentativeScenarioCatalog.cs`
- `src/Arius.E2E.Tests/Scenarios/RepresentativeScenarioRunner.cs`

Likely add:

- `src/Arius.E2E.Tests/Workflows/RepresentativeWorkflowDefinition.cs`
- `src/Arius.E2E.Tests/Workflows/RepresentativeWorkflowCatalog.cs`
- `src/Arius.E2E.Tests/Workflows/RepresentativeWorkflowRunner.cs`
- `src/Arius.E2E.Tests/Workflows/RepresentativeWorkflowState.cs`
- `src/Arius.E2E.Tests/Workflows/Steps/` for the typed step records and executors

### Update representative tests

Refactor:

- `src/Arius.E2E.Tests/RepresentativeArchiveRestoreTests.cs`
- `src/Arius.E2E.Tests/ArchiveTierRepresentativeTests.cs`

Desired end state:

- `RepresentativeArchiveRestoreTests.cs` runs the canonical workflow on Azurite and Azure
- archive-tier assertions are part of the same workflow, but the Azure-only assertions remain capability-gated in execution
- `ArchiveTierRepresentativeTests.cs` may disappear entirely if it no longer adds unique value

### Reuse existing helper code where stable

Preserve and adapt:

- current dataset generation under `Datasets/`
- current restore tree assertions
- current archive-tier sideload helper logic from the existing runner
- current backend fixture abstraction

### Remove obsolete code

The implementation should delete or simplify representative-suite code that no longer serves the new workflow model.

Expected cleanup:

- remove the old representative scenario definition/catalog/runner types once the workflow runner replaces them
- remove top-level representative tests that only existed to support the isolated-scenario model
- remove archive-tier representative test code if it becomes redundant with the canonical workflow
- keep only helpers that are still exercised by the new workflow

## Testing Strategy

The rewrite should be verified in layers:

1. step executor tests or narrow workflow tests for core runner behavior if needed
2. Azurite execution of the canonical workflow
3. Azure execution of the canonical workflow when credentials are available
4. full E2E suite

The workflow runner should fail with messages that identify the step name, expected repository version, and backend capability context.

## Non-Goals

- adding benchmark code now
- introducing a general-purpose workflow DSL
- adding a large matrix of top-level representative workflows
- asserting brittle exact counts of chunks, tar bundles, filetrees, or chunk-index shards
- replacing integration tests that own narrower product concerns
- preserving the old isolated representative scenario framework once the workflow runner is in place

## Open Decisions Resolved By This Design

- use one canonical workflow, not separate workflows per concern
- use typed step executors, not a hardcoded monolithic method
- include `--no-pointers` and `--remove-local` as optional typed substeps within the canonical workflow
- assert stable remote repository/container state in addition to file-system end-to-end behavior
- keep archive-tier behavior inside the same workflow behind backend capability gates
