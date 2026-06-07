---
description: Ground active OpenSpec change artifacts in the implemented code
---

Ground active OpenSpec change artifacts in the implementation that actually exists.

Use this after `/opsx-apply` and manual code edits, before `/opsx-verify`, `/opsx-sync`, or `/opsx-archive`.

**Input**: Optionally specify a change name after `/opsx-ground-spec` (e.g., `/opsx-ground-spec add-auth`). Optionally include a base ref in plain text, for example `since abc123` or `base main`. If omitted, check if the change can be inferred from conversation context. If vague or ambiguous, prompt for available active changes.

**Steps**

1. **Select the change**

   If no clear change name was provided, run:
   ```bash
   openspec list --json
   ```

   Use the **AskUserQuestion tool** to let the user select an active change. Do not guess when more than one active change exists.

2. **Load OpenSpec context**

   Run:
   ```bash
   openspec status --change "<name>" --json
   openspec instructions apply --change "<name>" --json
   ```

   Read every artifact path returned in `contextFiles`, including proposal, specs, design, and tasks where present. Also inspect `openspec/changes/<name>/.openspec.yaml`.

3. **Determine the implementation baseline**

   Use this order:

   - explicit user-supplied base ref
   - a clear base field in `.openspec.yaml`, such as `baseCommit`, `baseRef`, or `implementationBase`
   - inferred first commit that introduced `openspec/changes/<name>/`
   - ask the user if inference is ambiguous

   Useful commands:
   ```bash
   git log --oneline -- "openspec/changes/<name>"
   git log --diff-filter=A --format="%H %ad %s" --date=iso -- "openspec/changes/<name>/.openspec.yaml"
   git status --short
   ```

4. **Collect implementation evidence**

   Inspect committed and uncommitted changes:
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

   If the diff is large, start from `--stat` and `--name-status`, then inspect focused diffs and relevant files.

5. **Classify evidence**

   Classify changed files and behavior as:

   - product behavior or API change
   - test/scenario coverage evidence
   - refactor with no requirement impact
   - documentation/config/tooling change
   - OpenSpec artifact change
   - probably unrelated to this OpenSpec change

   Build an internal evidence map from implementation evidence to artifact impact. Every artifact update must cite changed code, changed tests, a diff hunk, or an explicit user decision.

6. **Update active change artifacts only**

   Edit only files under:
   ```text
   openspec/changes/<name>/
   ```

   Do not edit main specs under `openspec/specs/`. `/opsx-sync` and `/opsx-archive` own that.

   Update:

   - `specs/<capability>/spec.md` for implemented requirements and scenarios, preserving ADDED/MODIFIED/REMOVED/RENAMED delta format.
   - `design.md` for final decisions, tradeoffs, responsibility boundaries, and implementation deviations.
   - `tasks.md` for completed tasks and remaining follow-ups, based on evidence.
   - `proposal.md` only when implemented scope, capabilities, or impact changed.

7. **Ask before ambiguous edits**

   Ask the user before editing artifacts when:

   - the baseline cannot be determined reliably
   - behavior appears accidental or unrelated
   - implementation contradicts project/domain guidance
   - multiple capabilities could own the requirement
   - implementation added behavior outside the apparent proposal scope

8. **Validate and summarize**

   Run:
   ```bash
   openspec validate <name> --strict
   ```

   Summarize:

   - baseline used
   - implementation evidence inspected
   - artifacts updated
   - requirements/scenarios added, modified, removed, or left unchanged
   - ambiguities or follow-up tasks
   - validation result

   Recommend `/opsx-verify <name>` next.

**Guardrails**

- Ground artifacts in code evidence, not assumptions.
- Update only `openspec/changes/<name>/`.
- Do not edit `openspec/specs/`, sync specs, or archive.
- Do not make application code changes unless explicitly asked after grounding reveals an issue.
- Preserve unrelated user changes.
- Prefer small artifact edits over broad rewrites.
