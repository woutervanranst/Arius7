---
status: accepted
date: 2026-04-26
decision-makers: Wouter Van Ranst, OpenCode
---

# Skip Snapshot Publication For No-Op Archives

## Context and Problem Statement

Arius snapshots are repository commit points. Re-archiving an unchanged source tree currently builds the same filetree root hash but still publishes a new snapshot with a new timestamp.

The question for this ADR is whether an archive run that produces the same root hash as the latest snapshot should create a new snapshot, or preserve the existing latest snapshot.

## Decision Drivers

* snapshots should represent durable repository state changes
* unchanged archive runs should be idempotent and avoid creating redundant repository history
* snapshot history should remain meaningful for restore and list operations
* file timestamp metadata drift should not turn unchanged backup content into a new repository version
* no-op behavior should be explicit in integration and representative end-to-end coverage
* archive must still complete all durability work before deciding whether a new snapshot is needed

## Considered Options

* Always publish a new snapshot for every successful archive
* Skip snapshot publication when the newly built root hash matches the latest snapshot
* Add a separate no-op marker snapshot type

## Decision Outcome

Chosen option: "Skip snapshot publication when the newly built root hash matches the latest snapshot", because it keeps snapshots as meaningful commit points while preserving idempotent archive behavior for unchanged repositories. Filetree root identity is based on entry names, entry types, and content hashes; timestamp metadata remains serialized for restore/list consumers, but timestamp-only drift does not create a new root hash.

### Consequences

* Good, because repeated archives of unchanged data do not create redundant snapshot manifests, even when local filesystem timestamps drift.
* Good, because restore and list history remains focused on actual repository state changes.
* Good, because no-op archive results can point at the existing latest snapshot for compatibility with callers that expect a successful archive to have a snapshot timestamp.
* Bad, because callers cannot infer that a new snapshot was created purely from archive success; they must compare the returned snapshot version with the previously known latest version when that distinction matters.

### Confirmation

The decision is being followed when integration coverage archives an unchanged repository twice and observes one snapshot, and representative end-to-end coverage treats the no-op archive as preserving the current latest snapshot version.

## Pros and Cons of the Options

### Always publish a new snapshot for every successful archive

This is the previous behavior.

* Good, because every archive command has a unique snapshot timestamp.
* Bad, because unchanged runs create redundant repository history.
* Bad, because no-op archives look like meaningful commits even when the root filetree did not change.

### Skip snapshot publication when the newly built root hash matches the latest snapshot

This is the chosen design.

* Good, because snapshot history records state changes rather than command invocations.
* Good, because archive remains retry-friendly and idempotent for unchanged input.
* Neutral, because archive still scans and rebuilds the manifest before it can prove the root hash is unchanged.
* Bad, because callers that want to know whether a new snapshot was published need to compare versions or use future result metadata.

### Add a separate no-op marker snapshot type

This would record every archive invocation while distinguishing no-op runs from state-changing snapshots.

* Good, because command history would be complete.
* Bad, because it adds another repository record type without a current restore or durability need.
* Bad, because it complicates snapshot listing semantics for little user value.

## More Information

This ADR refines ADR-0001. The representative workflow still covers no-op re-archive behavior, but the intended behavior is now that a no-op archive preserves the existing latest snapshot instead of producing a new one.
