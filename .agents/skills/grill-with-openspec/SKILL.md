---
name: grill-with-openspec
description: Grilling session that challenges an OpenSpec change against the existing domain model, sharpens terminology, and updates the change artifacts and ADRs inline as decisions crystallise. Use when user wants to stress-test an OpenSpec change against their project's language and documented decisions, or mentions "grill me".
---

<what-to-do>

Interview me relentlessly about every aspect of this OpenSpec change until we reach a shared understanding. Walk down each branch of the design tree, resolving dependencies between decisions one-by-one. For each question, provide your recommended answer.

Ask the questions one at a time, waiting for feedback on each question before continuing.

If a question can be answered by exploring the codebase, explore the codebase instead.

</what-to-do>

<supporting-info>

## Context to load first

This skill operates on an OpenSpec change. Before grilling, load the existing domain model so you can challenge against it:

- **The change under discussion** — `openspec/changes/<name>/` (`proposal.md`, `design.md`, `tasks.md`, and `specs/<capability>/spec.md`). This is what you are grilling and what you update inline.
- **Capability specs** — `openspec/specs/*/spec.md`. These are the established, accepted requirements for each capability. Treat them as the source of truth for what the system already promises and for the canonical terminology in use.
- **ADRs** — `docs/decisions/adr-*.md`. These record past architectural decisions and the trade-offs behind them. Use them to detect when the plan contradicts or supersedes an earlier decision.

If the user has not told you which change they are grilling, ask which `openspec/changes/<name>/` directory to use before starting.

## During the session

### Challenge against the established language

When the user uses a term that conflicts with the canonical terminology in `openspec/specs/*/spec.md` or `AGENTS.md`, call it out immediately. "The `chunk-index-service` spec calls this a 'shard', but you seem to mean the whole index — which is it?"

### Sharpen fuzzy language

When the user uses vague or overloaded terms, propose a precise canonical term that matches the existing specs. "You're saying 'blob' — do you mean the large chunk or the tar chunk? The domain language distinguishes them."

### Discuss concrete scenarios

When domain relationships are being discussed, stress-test them with specific scenarios. Invent scenarios that probe edge cases and force the user to be precise about the boundaries between concepts. These often map directly onto the `#### Scenario:` blocks in the change's spec deltas.

### Cross-reference with code and specs

When the user states how something works, check whether the code and the accepted capability specs agree. If you find a contradiction, surface it: "The `restore-pipeline` spec says restore is read-only and never creates containers, but this change has restore creating a container — which is right?"

### Cross-reference with ADRs

When a decision in the plan touches a previously recorded decision, surface the relevant ADR. If the plan contradicts an accepted ADR, make the conflict explicit and decide whether the ADR is being superseded (and therefore needs updating) or the plan is wrong.

### Update the change inline

When a decision is resolved, update the OpenSpec change artifacts right there. Don't batch them up — capture them as they happen:

- Sharpen the **why/what** in `proposal.md`.
- Record resolved decisions and rejected alternatives in `design.md` (use the existing `### Decision` / "Alternative considered" style already present in the change).
- Adjust `tasks.md` when scope changes.
- Update or add requirements and `#### Scenario:` blocks in `specs/<capability>/spec.md` when behavior is clarified.

Match the structure and tone of the existing artifacts in the change. Keep the spec deltas in OpenSpec's `## ADDED/MODIFIED/REMOVED Requirements` format with `SHALL` requirement text and `#### Scenario:` blocks.

## Offer ADRs sparingly

Only offer to create an ADR when all three are true:

1. **Hard to reverse** — the cost of changing your mind later is meaningful.
2. **Surprising without context** — a future reader will look at the code and wonder "why on earth did they do it this way?"
3. **The result of a real trade-off** — there were genuine alternatives and you picked one for specific reasons.

If a decision is easy to reverse, skip it — you'll just reverse it. If it's not surprising, nobody will wonder why. If there was no real alternative, there's nothing to record beyond "we did the obvious thing."

### What qualifies

- **Architectural shape.** "Chunk-index responsibilities are split into read-through cache and archive write-session components."
- **Integration patterns between components.** "Features decide when; Shared decides how."
- **Technology choices that carry lock-in.** Storage backend, serialization format, auth provider. Not every library — just the ones that would take a quarter to swap out.
- **Boundary and scope decisions.** "`restore` and `ls` are read-only and must not create containers." The explicit no-s are as valuable as the yes-s.
- **Deliberate deviations from the obvious path.** Anything where a reasonable reader would assume the opposite. These stop the next engineer from "fixing" something that was deliberate.
- **Constraints not visible in the code.** Compliance, performance contracts, durability guarantees.
- **Rejected alternatives when the rejection is non-obvious.** Otherwise someone will suggest the rejected option again in six months.

### Writing the ADR

When an ADR is warranted, create it in `docs/decisions/` using the template at [docs/decisions/adr-template.md](../../../docs/decisions/adr-template.md). Number it sequentially: scan `docs/decisions/` for the highest existing `adr-NNNN-*.md` and increment by one. Keep ADRs pithy, assertive, and focused on the decision and its trade-offs.

If the change supersedes an accepted ADR, update the old ADR's `status`/`superseded-by` frontmatter and reference it from the new one.

</supporting-info>