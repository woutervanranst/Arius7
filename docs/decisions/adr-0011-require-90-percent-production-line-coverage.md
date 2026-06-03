---
status: "accepted"
date: 2026-06-03
decision-makers: ["Wouter Van Ranst"]
consulted: ["GitHub Copilot"]
informed: ["Arius maintainers"]
confidence: "high"
---

# Require 90 Percent Overall Production Line Coverage

## Context and Problem Statement

Arius is an agentic rewrite where tests carry much of the human review attention. ADR-0001 defines representative end-to-end coverage, ADR-0005 defines scoped advisory mutation testing, and ADR-0009 defines the fixture structure that keeps integration and E2E coverage maintainable. Coverage is already visible through Codecov, but Arius needs an explicit minimum so it remains a release-quality signal instead of a passive badge.

The question for this ADR is what minimum automated line coverage standard Arius should require for production code.

## Decision Drivers

* Tests are the main review surface for important behavior.
* Coverage should fail loudly when it drops below an agreed floor.
* Test projects and shared test infrastructure should not count as production code.
* Line coverage should complement behavior review and mutation testing, not replace them.

## Considered Options

* Keep coverage visible but advisory.
* Require 90 percent overall production line coverage.
* Require a stricter per-project or per-file coverage gate.

## Decision Outcome

Chosen option: "Require 90 percent overall production line coverage", because it creates a clear quality floor while avoiding the noise of per-file gates.

Confidence: high. The target matches the repository's test-first posture and the existing Codecov workflow.

The practical effect of this decision should be visible at a glance:

Before:

```text
Coverage reports are uploaded and visible, but overall production line coverage below 90 percent is not a documented release-quality failure.
```

After:

```text
Overall production line coverage below 90 percent fails the coverage gate and must be fixed or explicitly reconsidered by a later ADR.
```

### Consequences and Tradeoffs

* Good, because coverage regressions become a policy failure rather than a cosmetic badge change.
* Good, because an overall gate leaves room for deliberate low-value exclusions without making every file a brittle threshold boundary.
* Bad, because overall line coverage can hide localized risk in complex code.
* Bad, because a percentage can incentivize shallow tests unless reviews still judge assertion quality.

### Confirmation

This decision is being followed when all of the following are true:

* CI or Codecov enforces a 90 percent minimum for overall production line coverage.
* The denominator excludes test projects and reusable test infrastructure, including `src/*.Tests/**` and `src/Arius.Tests.Shared/**`.
* Coverage reports continue to be produced by runners where `dotnet-coverage` produces usable data.
* Reviews still evaluate assertion quality and behavior coverage; passing the percentage gate alone is not treated as proof that important behavior is tested.

## Pros and Cons of the Options

### Keep coverage visible but advisory

Coverage remains available through CI uploads and the README badge, but it does not block merges or releases.

* Good, because it avoids build noise from coverage tooling and reporting differences.
* Good, because maintainers can interpret coverage alongside test quality and product risk.
* Bad, because coverage can drift downward without creating a clear corrective moment.
* Bad, because Arius's agentic engineering model depends on tests staying central rather than optional.

### Require 90 percent overall production line coverage

This is the chosen design.

* Good, because it creates one clear repository-level floor for production code.
* Good, because it fits the current Codecov-oriented workflow and existing test-project exclusions.
* Good, because it avoids high-noise per-file failures while still making broad regression visible.
* Bad, because localized coverage gaps still need review, CRAP analysis, or targeted tests to identify.

### Require a stricter per-project or per-file coverage gate

Each production project or source file must independently remain at or above 90 percent line coverage.

* Good, because localized gaps cannot hide behind broad repository coverage.
* Bad, because small files, generated glue, platform-specific code, and defensive branches can create noisy failures.
* Bad, because it pushes the team toward exclusion management rather than behavior-focused testing.

## More Information

This ADR complements ADR-0001, which defines representative end-to-end coverage; ADR-0005, which defines scoped advisory mutation testing for `Arius.Core`; and ADR-0009, which defines the fixture boundaries for integration and E2E tests.