---
name: openspec-ground-spec-in-code
description: Ground OpenSpec change artifacts in the implemented code. Use after implementation or manual edits to update proposal, design, tasks, and delta specs before verify/archive.
license: MIT
compatibility: Requires openspec CLI and git.
metadata:
  author: local
  version: "1.0"
---

Ground an active OpenSpec change in the implementation that actually exists.

Use this after `/opsx-apply` and any manual code edits, before `/opsx-verify`, `/opsx-sync`, or `/opsx-archive`.

This is an evidence-first artifact update workflow. The goal is not to prove the implementation is correct; that is `/opsx-verify`. The goal is to make the active change artifacts accurately describe the implemented behavior, including deliberate deviations discovered during implementation.

**Input**: Optionally specify a change name. Optionally specify a base ref in plain text, for example `since abc123` or `base main`. If the change name is omitted, check if it can be inferred from context. If vague or ambiguous, prompt for available active changes.

**Steps**

1. **Select the change**

   If no clear change name was provided:
   ```bash
   openspec list --json
   ```

   Use the **AskUserQuestion tool** to let the user select an active change.

   Show active changes with task progress/status. Do not guess or auto-select when more than one active change exists.

2. **Load OpenSpec context**

   Run:
   ```bash
   openspec status --change "<name>" --json
   openspec instructions apply --change "<name>" --json
   ```

   Read every artifact path returned in `contextFiles`, including proposal, specs, design, and tasks where present.

   Also inspect the change metadata file:
   ```text
   openspec/changes/<name>/.openspec.yaml
   ```

3. **Determine the implementation baseline**

   Establish the git base ref used to identify implementation changes.

   Use this order:

   - If the user supplied an explicit base ref, use it.
   - If `.openspec.yaml` contains a clear base field such as `baseCommit`, `baseRef`, or `implementationBase`, use it.
   - Otherwise infer the first commit that introduced the change artifacts, typically by inspecting git history for `openspec/changes/<name>/.openspec.yaml` and the change directory.
   - If the first artifact commit is found, use that commit as the baseline so `git diff <base>...HEAD` captures implementation work after the spec baseline.
   - If the first artifact commit cannot be found, or if artifacts and implementation appear to have been committed together, ask the user for the base ref before editing artifacts.

   Useful commands include:
   ```bash
   git log --oneline -- "openspec/changes/<name>"
   git log --diff-filter=A --format="%H %ad %s" --date=iso -- "openspec/changes/<name>/.openspec.yaml"
   git status --short
   ```

   Do not rely on uncommitted OpenSpec artifact edits as implementation evidence. Treat them as user-authored artifact changes and preserve them unless they conflict with the grounding update.

4. **Collect implementation evidence**

   Collect both committed and uncommitted implementation changes:
   ```bash
   git diff --stat <base>...HEAD
   git diff --name-status <base>...HEAD
   git diff <base>...HEAD
   git diff --cached --stat
   git diff --cached --name-status
   git diff --cached
   git diff --stat
   git diff --name-status
   git diff
   ```

   If the full diff is too large, first inspect `--stat` and `--name-status`, then read focused diffs and files by area. Use code search to follow renamed/moved symbols and behavior.

   Classify each changed file or behavior as one of:

   - Product behavior or API change.
   - Test or scenario coverage evidence.
   - Refactor with no requirement impact.
   - Documentation/config/tooling change.
   - OpenSpec artifact change.
   - Probably unrelated to this OpenSpec change.

5. **Build an evidence map**

   Create an internal map from implementation evidence to artifact updates:

   ```text
   Evidence -> Behavior -> Artifact impact -> Confidence -> Source files/tests
   ```

   Treat tests as strong evidence for scenarios. Treat production code as strong evidence for behavior. Treat names/comments alone as weak evidence.

   For every meaningful artifact edit, be able to cite at least one of:

   - changed production file path
   - changed test file path
   - git diff hunk
   - explicit user decision

6. **Update delta specs**

   Update only delta specs under:
   ```text
   openspec/changes/<name>/specs/<capability>/spec.md
   ```

   Do not edit main specs under `openspec/specs/`; `/opsx-sync` and `/opsx-archive` own that.

   Preserve the OpenSpec delta format:

   ```markdown
   ## ADDED Requirements

   ### Requirement: New Feature
   The system SHALL ...

   #### Scenario: Basic case
   - **WHEN** ...
   - **THEN** ...

   ## MODIFIED Requirements

   ### Requirement: Existing Feature
   ...

   ## REMOVED Requirements

   ### Requirement: Deprecated Feature

   ## RENAMED Requirements

   - FROM: `### Requirement: Old Name`
   - TO: `### Requirement: New Name`
   ```

   Delta spec update rules:

   - Add missing requirements or scenarios for behavior that is implemented and in scope.
   - Modify requirements or scenarios that no longer match implemented behavior.
   - Remove or narrow proposed requirements that were intentionally not implemented.
   - Prefer scenario-level updates over rewriting whole requirements when only examples changed.
   - Keep requirements behavioral and externally observable. Do not encode incidental private implementation details unless they are architectural constraints the project treats as requirements.
   - If behavior belongs to an existing capability, update that capability delta spec. If no suitable capability exists, ask before creating a new capability spec.

7. **Update design**

   If `design.md` exists, update it to match the implemented design.

   Capture:

   - final architecture and responsibility boundaries
   - important deviations from the original design
   - decisions made during implementation
   - tradeoffs that now matter for maintainers
   - rejected alternatives only when implementation evidence or user decisions make them relevant

   Remove stale design claims that contradict code. Do not turn the design into a file-by-file implementation log.

8. **Update tasks**

   If `tasks.md` exists:

   - Mark a task complete only when implementation evidence supports it.
   - Leave incomplete tasks unchecked when the behavior is absent or ambiguous.
   - Add follow-up tasks for discovered gaps, missing tests, incomplete docs, or explicit cleanup that remains necessary.
   - Do not mark tasks complete just because code changed nearby.

9. **Update proposal only when scope changed**

   If `proposal.md` exists, update it only when implementation changed the actual scope, capabilities, non-goals, or impact.

   Do not rewrite the proposal for wording style. Keep the proposal focused on why and what changed.

10. **Resolve ambiguity before editing**

   Ask the user before making artifact changes when:

   - the baseline cannot be determined reliably
   - a code change appears accidental or unrelated
   - implementation contradicts domain language or project guidance
   - multiple capabilities could own the requirement
   - the implementation added behavior outside the proposal's apparent scope
   - there is no test evidence for a behavior that changes requirements and the intent is unclear

11. **Validate and summarize**

   After editing artifacts, run:
   ```bash
   openspec validate <name> --strict
   ```

   Then summarize:

   - baseline used
   - implementation evidence inspected
   - artifacts updated
   - requirements/scenarios added, modified, removed, or left unchanged
   - ambiguities or follow-up tasks
   - validation result

   Recommend `/opsx-verify <name>` next.

**Output On Success**

```markdown
## Spec Grounded In Code: <change-name>

**Baseline:** <base-ref>
**Evidence:** <N files changed, M tests changed>
**Artifacts updated:** proposal/design/tasks/specs as applicable
**Validation:** `openspec validate <name> --strict` passed

### Spec Updates
- <capability>: added/modified/removed <requirement/scenario>

### Follow-ups
- <any remaining ambiguity or task>

Next: run `/opsx-verify <change-name>`.
```

**Guardrails**

- Update only the active change under `openspec/changes/<name>/`.
- Never sync to or edit `openspec/specs/` in this workflow.
- Never archive the change in this workflow.
- Do not make application code changes unless the user explicitly asks for fixes after grounding reveals a problem.
- Preserve unrelated user changes.
- Every artifact update must be grounded in implementation evidence or an explicit user decision.
- Prefer small, surgical artifact edits over broad rewrites.
- Keep the operation repeatable: running it again with the same baseline and implementation should not churn artifact wording.
