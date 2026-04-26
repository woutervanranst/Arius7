# No-Op Archive Snapshot Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make unchanged archive runs preserve the existing latest snapshot instead of publishing redundant snapshots.

**Architecture:** `ArchiveCommandHandler` remains responsible for deciding when to publish snapshots. Filetree hash identity ignores timestamp-only metadata drift and is based on names, entry types, and content hashes. After building the new filetree root hash, archive resolves the latest snapshot and skips `SnapshotService.CreateAsync` when the root hash is unchanged, returning the existing snapshot timestamp/root hash. Tests assert the repository snapshot count and representative workflow state handling.

**Tech Stack:** .NET 10, C#, TUnit, Shouldly, Azurite/Testcontainers, ADR markdown.

---

### Task 1: Add Integration Regression Test

**Files:**
- Modify: `src/Arius.Integration.Tests/Pipeline/RoundtripTests.cs`

- [x] **Step 1: Add a test that captures no-op snapshot intent**

Add a test near the incremental archive tests:

```csharp
[Test]
public async Task Archive_UnchangedRepository_DoesNotCreateNewSnapshot()
{
    await using var fix = await PipelineFixture.CreateAsync(azurite);

    fix.WriteFile("file.bin", "stable"u8.ToArray());

    var first = await fix.ArchiveAsync();
    first.Success.ShouldBeTrue(first.ErrorMessage);

    var snapshotCountAfterFirst = await fix.BlobContainer.ListAsync(BlobPaths.Snapshots).CountAsync();
    snapshotCountAfterFirst.ShouldBe(1);

    var second = await fix.ArchiveAsync();
    second.Success.ShouldBeTrue(second.ErrorMessage);

    var snapshotCountAfterSecond = await fix.BlobContainer.ListAsync(BlobPaths.Snapshots).CountAsync();
    snapshotCountAfterSecond.ShouldBe(1);
    second.RootHash.ShouldBe(first.RootHash);
    second.SnapshotTime.ShouldBe(first.SnapshotTime);
}
```

- [x] **Step 2: Run targeted test and confirm it fails before production fix**

Run: `dotnet test --project src/Arius.Integration.Tests/Arius.Integration.Tests.csproj -c Release --treenode-filter "/*/*/RoundtripTests/Archive_UnchangedRepository_DoesNotCreateNewSnapshot"`

Expected before implementation: FAIL because snapshot count after second archive is `2` or the second timestamp differs.

### Task 2: Implement No-Op Snapshot Skip

**Files:**
- Modify: `src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs`

- [x] **Step 1: Resolve latest snapshot before creating a new snapshot**

Replace the snapshot creation block with logic equivalent to:

```csharp
if (rootHash is not null)
{
    var latestSnapshot = await _snapshotSvc.ResolveAsync(cancellationToken: cancellationToken);
    if (latestSnapshot?.RootHash == rootHash)
    {
        snapshotRootHash = latestSnapshot.RootHash;
        snapshotTime = latestSnapshot.Timestamp;
        _logger.LogInformation("[snapshot] Unchanged: {Timestamp} rootHash={RootHash}", latestSnapshot.Timestamp.ToString("o"), latestSnapshot.RootHash[..8]);
    }
    else
    {
        var snapshot = await _snapshotSvc.CreateAsync(rootHash, filesScanned, totalSize, cancellationToken: cancellationToken);
        snapshotRootHash = snapshot.RootHash;
        snapshotTime = snapshot.Timestamp;
        _logger.LogInformation("[snapshot] Created: {Timestamp} rootHash={RootHash}", snapshot.Timestamp.ToString("o"), snapshot.RootHash[..8]);

        await _mediator.Publish(new SnapshotCreatedEvent(rootHash, snapshot.Timestamp, snapshot.FileCount), cancellationToken);
    }
}
```

- [x] **Step 2: Run targeted integration test**

Run: `dotnet test --project src/Arius.Integration.Tests/Arius.Integration.Tests.csproj -c Release --treenode-filter "/*/*/RoundtripTests/Archive_UnchangedRepository_DoesNotCreateNewSnapshot"`

Expected: PASS.

### Task 3: Update Representative E2E State Handling

**Files:**
- Modify: `src/Arius.E2E.Tests/Workflows/Steps/ArchiveStep.cs`
- Modify: `src/Arius.E2E.Tests/Workflows/Steps/AssertRemoteStateStep.cs`

- [x] **Step 1: Make archive state update preserve latest version for no-op results**

In `ArchiveStep.ExecuteAsync`, compute `resultVersion` from `result.SnapshotTime`. Set `PreviousSnapshotVersion` only when `resultVersion` differs from `LatestSnapshotVersion`; otherwise leave both version fields unchanged.

- [x] **Step 1a: Make representative no-op setup explicit**

In `RepresentativeWorkflowCatalog`, materialize `SyntheticRepositoryVersion.V2` immediately before `archive-v2-noop` so the no-op assertion operates on an explicitly unchanged V2 source tree.

- [x] **Step 1b: Rebuild missing source fixtures after cache reset**

In `MaterializeVersionStep`, if V2 materialization needs V1 and the recorded V1 root was removed by fixture recreation, rematerialize V1 into `representative-source/V1` before deriving V2.

- [x] **Step 2: Update no-op assertion snapshot count**

In `AssertRemoteStateStep`, change the no-op snapshot count expectation from `3`/implicit latest advancement to preserving the existing latest snapshot count for the workflow. The canonical workflow has two snapshots before no-op, so assert `2` for `RemoteAssertionKind.NoOpArchive`.

- [x] **Step 3: Run representative E2E test**

Run: `dotnet test --project src/Arius.E2E.Tests/Arius.E2E.Tests.csproj -c Release --treenode-filter "/*/*/RepresentativeArchiveRestoreTests/*"`

Expected: PASS or Azure skipped if credentials are unavailable.

### Task 4: Update ADR And Human/Agent Docs

**Files:**
- Modify: `docs/decisions/adr-0001-structure-representative-e2e-coverage.md`
- Create: `docs/decisions/adr-0002-skip-snapshots-for-no-op-archives.md`
- Modify: `README.md`
- Modify: `AGENTS.md`

- [x] **Step 1: Update ADR-0001 confirmation**

Change the no-op confirmation line to say no-op re-archive preserves the latest snapshot when the root hash is unchanged, and reference ADR-0002.

- [x] **Step 2: Update README high-level behavior**

Add one human-readable sentence in the end-to-end test description or snapshot section saying unchanged archive runs preserve the current latest snapshot instead of adding a redundant version.

- [x] **Step 3: Update AGENTS guidance**

Add an E2E guidance bullet saying no-op archive coverage should assert snapshot preservation, not new snapshot creation.

### Task 5: Verify

**Files:**
- No direct edits.

- [x] **Step 1: Run targeted tests**

Run both targeted commands from Tasks 2 and 3.

- [x] **Step 2: Run all tests required by project guidance**

On non-Windows run all non-Windows test projects with `dotnet test --project <project> -c Release`, skipping `Arius.Explorer.Tests`.

- [x] **Step 3: Run slopwatch**

Run: `slopwatch analyze --fail-on warning`.

Expected: 0 new issues.
