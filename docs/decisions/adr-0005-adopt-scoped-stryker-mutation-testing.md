---
status: accepted
date: 2026-04-29
decision-makers: Wouter Van Ranst, OpenCode
---

# Adopt Scoped Stryker Mutation Testing For Arius.Core

## Context and Problem Statement

The repository now has a working Stryker.NET setup for mutation testing, but Arius uses TUnit on Microsoft Testing Platform and the current Stryker integration depends on the preview MTP runner. Mutation results also fluctuate between runs enough that they are useful for finding weak tests, but not yet stable enough to act as a hard quality gate.

The question for this ADR is how Arius should adopt mutation testing today so it provides durable value without overcommitting to unstable tooling behavior or broad scope.

## Decision Drivers

* mutation testing should help identify weak behavior-level tests in core repository logic
* Arius uses TUnit on Microsoft Testing Platform, so mutation tooling must work with that test platform reality
* mutation runs are significantly slower than the normal unit test suite
* fluctuating mutation scores should not become a noisy CI gate before their stability is understood
* the repository needs a durable architectural record that supersedes temporary superpowers implementation artifacts

## Considered Options

* Do not adopt Stryker yet
* Adopt Stryker broadly across the repository with CI enforcement
* Adopt Stryker in a scoped local/manual setup for `Arius.Core`

## Decision Outcome

Chosen option: "Adopt Stryker in a scoped local/manual setup for `Arius.Core`", because it gives Arius immediate mutation-testing value in the most important core logic without pretending that current tooling stability and runtime cost are ready for a broad enforced gate.

The current supported setup is:

* mutation testing targets `src/Arius.Core/Arius.Core.csproj`
* tests run through `src/Arius.Core.Tests/Arius.Core.Tests.csproj`
* Stryker uses the preview Microsoft Testing Platform runner via `"test-runner": "mtp"`
* mutation runs are local/manual for now
* mutation reports guide targeted test improvements, but score changes are advisory rather than release-gating

### Consequences

* Good, because Arius can use mutation testing immediately in the core archive, restore, list, snapshot, chunk, and tree behavior that matters most.
* Good, because the chosen scope keeps runtime and debugging costs bounded while the team learns where mutation testing pays off.
* Good, because the ADR reflects the repository's real TUnit/MTP setup instead of assuming a standard VSTest-only world.
* Good, because fluctuating scores are treated as diagnostic evidence instead of falsely precise pass/fail gates.
* Bad, because mutation testing is not yet a whole-repository signal.
* Bad, because developers must interpret score movement carefully when the preview MTP runner produces unstable results.
* Bad, because future CI enforcement requires a separate decision once score stability and runtime are better understood.

### Confirmation

The decision is being followed when `stryker-config.json` continues to target `Arius.Core` plus `Arius.Core.Tests`, uses the MTP runner, and developers run mutation testing manually with `dotnet stryker --config-file stryker-config.json` from the repository root.

The README should continue to describe Stryker as a local/manual Core-scoped tool. Mutation-driven test improvements should be verified with `dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj` plus a fresh Stryker rerun, but the resulting score should be treated as guidance rather than a hard gate until a later ADR says otherwise.

## Pros and Cons of the Options

### Do not adopt Stryker yet

This defers mutation testing until tooling and workflow questions are fully settled.

* Good, because it avoids time spent on unstable tooling behavior.
* Good, because no extra documentation or developer workflow is needed.
* Bad, because Arius loses a useful signal for weak tests in core repository behavior.
* Bad, because the team learns nothing about where mutation testing helps or where it produces noise.

### Adopt Stryker broadly across the repository with CI enforcement

This would make mutation score a larger quality gate immediately.

* Good, because it aims for broad repository-wide pressure toward stronger tests.
* Good, because CI enforcement can make mutation regressions visible automatically.
* Bad, because current MTP-preview instability and runtime cost make this premature.
* Bad, because integration-heavy and slower test areas would make early adoption noisy and expensive.

### Adopt Stryker in a scoped local/manual setup for `Arius.Core`

This is the chosen design.

* Good, because it focuses mutation testing on the highest-value core logic first.
* Good, because local/manual execution keeps tooling instability from blocking ordinary development.
* Good, because it creates room to learn from reports before adding thresholds or CI policy.
* Bad, because the signal is narrower and relies on manual follow-through.

## More Information

This ADR supersedes the Stryker-related superpowers documents as implementation artifacts, specifically:

* `docs/superpowers/specs/2026-04-29-stryker-core-design.md`
* `docs/superpowers/plans/2026-04-29-stryker-core.md`
* `docs/superpowers/specs/2026-04-29-stryker-coverage-improvement-design.md`
* `docs/superpowers/plans/2026-04-29-stryker-coverage-improvement.md`
* `docs/superpowers/specs/2026-04-29-stryker-adr-design.md`

Those documents were useful for planning and execution, but this ADR is now the durable source of truth for why Arius uses the current Stryker setup and how it should be interpreted.
