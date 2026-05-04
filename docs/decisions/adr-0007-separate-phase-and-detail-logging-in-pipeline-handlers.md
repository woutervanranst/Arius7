---
status: accepted
date: 2026-05-03
decision-makers: Wouter Van Ranst, OpenCode
---

# Separate Phase And Detail Logging In Pipeline Handlers

## Context and Problem Statement

`ArchiveCommandHandler` emits logs for both coarse pipeline progress and detailed per-step behavior. Before this decision, the handler mixed timing markers such as `[phase] tail flush-index` with separate completion logs such as `[index] Flush complete`, and also used different naming schemes for earlier and later stages.

The question for this ADR is how Arius pipeline handlers should structure operational logs so they remain readable to humans, useful for benchmark timing analysis, and consistent enough to apply across `archive`, `restore`, and `ls` flows.

## Decision Drivers

* benchmark-oriented phase timing should remain easy to read from logs
* logs should distinguish coarse pipeline progress from detailed operational events
* concurrent pipeline stages should not pretend to have simple sequential end boundaries when they overlap
* handlers should avoid redundant logs that restate the same event under two different tags
* the same logging taxonomy should be reusable in `ArchiveCommandHandler`, `RestoreCommandHandler`, and future `ls` pipeline work

## Considered Options

* Keep ad hoc mixed logging per handler
* Use coarse phase-entry logs plus category-specific detail logs
* Use start and end phase logs for every phase
* Flatten everything into category-specific logs without a separate phase taxonomy

## Decision Outcome

Chosen option: "Use coarse phase-entry logs plus category-specific detail logs", because it preserves readable benchmark timing markers while giving each handler a clear two-level logging taxonomy and avoiding redundant duplicate messages.

Pipeline handlers should use the following structure:

* `[archive]`, `[restore]`, or another top-level operation tag for whole-run lifecycle events such as start, done, and failure
* `[phase] <name>` for coarse phase boundaries that indicate when a major stage becomes active
* category-specific tags such as `[scan]`, `[hash]`, `[dedup]`, `[upload]`, `[tar]`, `[tree]`, and `[snapshot]` for detailed events that happen within a phase

`[phase]` logs are phase-entry markers, not full begin/end spans. This is intentional. Several Arius pipeline stages overlap or run concurrently, so logging an "end" for every phase would imply a simpler sequential model than the handlers actually implement. For overlapping stages, the useful signal is when a phase becomes active, not a misleading synthetic completion boundary.

Category-specific logs should remain only when they add information beyond the phase marker. A detail log such as `[snapshot] Created: ...` is useful because it records the outcome within the `snapshot` phase. A log such as `[index] Flush complete` is redundant when `[phase] flush-index` already identifies the coarse stage and no extra payload is added.

This decision does not require the benchmark project to parse `[phase]` logs. The benchmark code currently records BenchmarkDotNet summary output separately. Phase logs remain an operational timing aid for human analysis and for any later tooling that chooses to interpret them.

### Consequences

* Good, because pipeline logs now have a stable hierarchy: operation lifecycle, coarse phase boundaries, and detailed in-phase events.
* Good, because benchmark-oriented timing markers remain present without depending on a separate parser contract today.
* Good, because redundant completion logs that add no information can be removed.
* Good, because the same structure can be applied consistently in `archive`, `restore`, and `ls` handlers.
* Bad, because phase durations are inferred from successive markers rather than explicit begin/end pairs.
* Bad, because some category tags still require judgment about whether they add real detail or merely restate the phase.

### Confirmation

The decision is being followed when pipeline handlers use:

* one top-level operation tag for lifecycle events
* `[phase] <name>` markers for major stage entry points
* category-specific detail logs only when they add information beyond the phase marker

The decision is not being followed when a handler mixes multiple incompatible phase naming schemes, emits both a phase marker and a second completion log that restates the same event without extra payload, or introduces end-of-phase markers for overlapping concurrent phases.

In `ArchiveCommandHandler`, this means the handler should emit phase markers such as `[phase] hash` and detailed logs such as `[hash] {Path} -> {Hash} ({Size})`, while avoiding redundant pairs such as `[phase] flush-index` followed by `[index] Flush complete`.

Tests should verify the agreed coarse phase names for handlers that expose benchmark-relevant phase logging. Code review should check that newly added handler logs fit the same taxonomy before similar changes are applied to `RestoreCommandHandler` or `ls` flows.

## Pros and Cons of the Options

### Keep ad hoc mixed logging per handler

This leaves each handler free to evolve logging independently.

* Good, because it has no immediate migration cost.
* Bad, because handlers drift into inconsistent naming and redundant logs.
* Bad, because benchmark timing interpretation becomes handler-specific and harder to compare.

### Use coarse phase-entry logs plus category-specific detail logs

This is the chosen design.

* Good, because it cleanly separates timing boundaries from detailed operational messages.
* Good, because it models concurrent pipelines honestly by logging activation rather than pretending every phase has a simple sequential end.
* Good, because it is small enough to apply consistently across handlers.
* Bad, because reading exact phase durations still requires comparing timestamps between nearby log entries.

### Use start and end phase logs for every phase

This would treat each phase as a full span with explicit begin and complete messages.

* Good, because sequential phases would have directly visible duration boundaries.
* Bad, because overlapping pipeline stages would produce misleading or ambiguous "end" semantics.
* Bad, because it increases log noise without solving the concurrency modeling problem.

### Flatten everything into category-specific logs without a separate phase taxonomy

This would keep tags such as `[hash]`, `[upload]`, and `[snapshot]` but drop explicit coarse phase markers.

* Good, because it keeps one less log concept in the system.
* Bad, because benchmark-oriented coarse stage timing becomes harder to read.
* Bad, because high-volume detail logs can bury major pipeline transitions.

## More Information

This ADR defines the durable logging principle for Arius pipeline handlers. It is expected to guide future cleanup of `RestoreCommandHandler` and any future `ls` pipeline logging so those handlers reuse the same taxonomy instead of inventing local conventions.

Related decisions:

* ADR-0006 improves filetree staging and build scalability in the archive tail, where coarse phase logging remains useful for understanding post-upload work.
