---
status: accepted
date: 2026-04-24
decision-makers: Wouter Van Ranst, OpenCode
---

# Cover Real Archive Behavior With A Representative End-To-End Test Suite

## Context and Problem Statement

Arius is a backup and archive tool. It needs end-to-end coverage that proves more than isolated commands in isolation: the product must be able to create a real archive, evolve that archive over time, and restore the expected repository state correctly.

The question for this ADR is how to design representative end-to-end tests so they validate real archive behavior with strong confidence, while still remaining deterministic and practical to run in development and CI.

## Decision Drivers

* end-to-end coverage should validate one realistic archive history rather than isolated one-off operations
* the same representative story should run on both Azurite and Azure where backend capabilities overlap
* Azure-only behaviors, especially archive-tier restore behavior, must be exercised against the real backend
* the representative repository must be deterministic so failures are reproducible
* assertions should focus on archive and restore behavior, snapshot lineage, and other stable invariants rather than brittle storage-layout details
* the suite should cover the main user-visible archive lifecycle, not just happy-path single-command checks

## Considered Options

* Test only isolated archive and restore scenarios
* Build a representative matrix of many separate end-to-end scenarios
* Build one canonical representative workflow that exercises one evolving archive history on Azurite and Azure

## Decision Outcome

Chosen option: "Build one canonical representative workflow that exercises one evolving archive history on Azurite and Azure", because it gives the strongest end-to-end evidence that Arius can create, extend, and restore a real archive while keeping the suite deterministic and shared across both backends.

### Consequences

* Good, because the suite now proves that Arius can archive a repository, mutate it, archive again, and restore both latest and previous states correctly.
* Good, because the same representative workflow runs on Azurite and Azure, which keeps the main archive story consistent across both backends.
* Good, because Azure-specific archive-tier behavior is still tested on real Azure storage rather than being approximated in Azurite.
* Good, because the workflow covers warm restore, cold restore, no-op re-archive, pointer-file behavior, overwrite/no-overwrite conflict behavior, `--remove-local`, and archive-tier pending-versus-ready restore behavior in one coherent history.
* Good, because the deterministic synthetic repository lets the suite make strong behavioral assertions without relying on ad hoc random data.
* Good, because the assertions emphasize stable product behavior such as snapshot lineage, restored content, deduplication behavior, and cleanup behavior.
* Bad, because a representative workflow is broader and slower than a narrow one-scenario test.
* Bad, because when such a workflow fails, diagnosis depends on clear step boundaries and targeted assertions.

### Confirmation

The decision is being followed when the representative suite demonstrates all of the following:

* Arius can archive a deterministic `V1` repository and restore it correctly.
* Arius can archive a deterministic `V2` evolution of that same repository and restore the latest state correctly.
* Arius can restore the previous snapshot correctly after the archive history has advanced.
* The same representative workflow runs on both Azurite and Azure for shared behavior.
* Cold-cache and warm-cache restore behavior are both exercised against the same archive history.
* No-op re-archive behavior preserves stable repository structure and preserves the current latest snapshot when the root hash is unchanged, as refined by ADR-0002.
* Pointer-file expectations are verified for normal archive behavior and for `--no-pointers` behavior.
* Local conflict behavior is verified for both overwrite and no-overwrite restore paths.
* `--remove-local` behavior is exercised as part of the archive lifecycle.
* Archive-tier behavior on Azure proves both the pending rehydration path and the ready restore plus cleanup path.

## Pros and Cons of the Options

### Test only isolated archive and restore scenarios

This approach focuses on narrow end-to-end checks such as one archive test, one restore test, and a few one-off probes.

* Good, because the tests are smaller and easier to diagnose.
* Good, because runtime is usually lower.
* Bad, because it does not prove that Arius behaves correctly across one evolving archive history.
* Bad, because previous-version restore, no-op re-archive, cold versus warm cache transitions, and archive lifecycle interactions become fragmented or missed.

### Build a representative matrix of many separate end-to-end scenarios

This approach models many archive and restore cases, but each case is run as its own isolated scenario.

* Good, because it can enumerate many conditions explicitly.
* Good, because each scenario can target one concern.
* Neutral, because it still gives more coverage than narrow one-off tests.
* Bad, because it weakens the main representative story: one real archive evolving over time.
* Bad, because cold and warm cache behavior, snapshot history, and repeated archive operations are treated as disconnected setup instead of part of one repository lifecycle.

### Build one canonical representative workflow that exercises one evolving archive history on Azurite and Azure

This is the chosen design.

* Good, because it tests the product the way users experience it: as a repository that is archived repeatedly and restored later.
* Good, because it gives one coherent end-to-end story shared by Azurite and Azure.
* Good, because it keeps Azure-only archive-tier semantics in the same representative strategy while still testing them on the real backend.
* Bad, because it is broader, slower, and more involved than a small isolated scenario.

## More Information

The representative suite is intentionally built around a deterministic synthetic repository and a canonical workflow rather than ad hoc random data or a large disconnected scenario list.

Azurite provides shared representative backend coverage that can run locally and in CI. Azure provides the real-service path, including archive-tier behavior that cannot be represented faithfully on Azurite. Together they give Arius one end-to-end test strategy that is both practical and behaviorally meaningful.

This ADR captures the implemented outcome of the PR after several iterations recorded in:

* `docs/superpowers/specs/2026-04-20-shared-test-infrastructure-design.md`
* `docs/superpowers/specs/2026-04-23-representative-workflow-design.md`
