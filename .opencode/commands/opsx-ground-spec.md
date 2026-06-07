---
description: Ground active OpenSpec change artifacts in the implemented code
---

Ground active OpenSpec change artifacts in the implementation that actually exists.

Use this after `/opsx-apply` and manual code edits, before `/opsx-verify`, `/opsx-sync`, or `/opsx-archive`.

**Input**: Optionally specify a change name after `/opsx-ground-spec` (e.g., `/opsx-ground-spec add-auth`). Optionally include a base ref in plain text, for example `since abc123` or `base main`. If omitted, check if the change can be inferred from conversation context. If vague or ambiguous, prompt for available active changes.

## Core principle — write a clairvoyant spec

Grounding rewrites the change artifacts so that **a fresh implementation built from them would reproduce the code that exists today**. The grounded spec must read as if it had always, perfectly described the real implementation — as if its author were clairvoyant. It is NOT a changelog, a correction log, or a diff against the earlier spec. The history of how the spec evolved lives in git; never narrate it inside the artifacts.

Apply these rules to every artifact edit:

- **No history, no notes.** Do NOT add "grounding note", "implementation deviations", "(deferred)", "(NOT DONE)", "the implementation omits X", "originally proposed Y but shipped Z", or any prose that describes how the code differs from a previous version of the spec. If you catch yourself writing *about* a change, stop and instead write the spec as the change never existed.
- **Unimplemented features are removed, not annotated.** A requirement, scenario, schema column, API member, design decision, or task that exists in the original spec but is absent from the code is DELETED from the grounded spec. Do not mark it deferred or as a follow-up. If it matters later, it becomes a new change with its own proposal.
- **Differently-implemented features are restated, not corrected.** When the code does something other than the original spec said, rewrite the requirement/scenario/design to describe the actual behavior directly, as if it were the intended design from the start. State the spec that would produce the real code — names, schema, signatures, and behavior matching what is there.
- **Internal consistency.** The result must contain no leftover references to removed features, no mix of old and new names, and no contradictions between proposal, design, specs, and tasks.

The conversational summary you give the user at the end MAY describe what you added/removed/changed — that is chat, not a spec artifact. The artifacts themselves stay clairvoyant.

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

   If the diff is large, start from `--stat` and `--name-status`, then inspect focused diffs and relevant files. The current code is the source of truth — read the actual implementation files, not just the diff.

5. **Classify evidence**

   Classify changed files and behavior as:

   - product behavior or API change
   - test/scenario coverage evidence
   - refactor with no requirement impact
   - documentation/config/tooling change
   - OpenSpec artifact change
   - probably unrelated to this OpenSpec change

   Build an internal evidence map from implementation evidence to artifact impact. Every artifact edit must be backed by code, tests, or an explicit user decision — but the citation stays in your reasoning, never in the artifact text.

6. **Rewrite active change artifacts only, clairvoyantly**

   Edit only files under:
   ```text
   openspec/changes/<name>/
   ```

   Do not edit main specs under `openspec/specs/`. `/opsx-sync` and `/opsx-archive` own that.

   Rewrite each artifact to describe the implementation as-built, following the Core principle:

   - `specs/<capability>/spec.md` — requirements and scenarios that match the real behavior, preserving the ADDED/MODIFIED/REMOVED/RENAMED delta format. Delete requirements/scenarios for behavior that was not built. Rewrite (do not annotate) requirements whose behavior shipped differently. Schema blocks, API signatures, and names must match the code exactly.
   - `design.md` — the final as-built design: the decisions, tradeoffs, and responsibility boundaries that describe the code that exists. Remove any "alternatives considered" or "deviation" framing that no longer reflects reality, and remove design for features that were not built. Do not add a deviations/history section.
   - `tasks.md` — the task list that, if followed, produces the implementation. Remove tasks for unbuilt features; restate tasks whose approach changed so they describe the work actually done. Keep it a coherent build plan, not a status report with struck-through or "NOT DONE" items.
   - `proposal.md` — update Why/What Changes/Capabilities/Impact so the stated scope equals what was actually built. Drop proposed scope that was not implemented.

7. **Ask before ambiguous or scope-shrinking edits**

   Use the AskUserQuestion tool before editing when:

   - the baseline cannot be determined reliably
   - behavior appears accidental or unrelated
   - implementation contradicts project/domain guidance
   - multiple capabilities could own the requirement
   - the code added substantial behavior outside the original proposal scope (clairvoyantly, this behavior should be folded into the spec — confirm it belongs in this change rather than a separate one)
   - removing an unimplemented feature would materially shrink the change's stated intent (confirm the feature was genuinely dropped rather than merely incomplete work in progress)

8. **Validate and summarize**

   Run:
   ```bash
   openspec validate <name> --strict
   ```

   Summarize for the user (in chat, not in the artifacts):

   - baseline used
   - implementation evidence inspected
   - artifacts rewritten
   - requirements/scenarios added, rewritten, or removed (and features dropped from scope)
   - decisions you needed from the user
   - validation result

   Recommend `/opsx-verify <name>` next.

**Guardrails**

- Produce a clairvoyant spec: it describes the implementation as the intended design, with no trace of the spec's own history.
- Ground artifacts in code evidence, not assumptions.
- Remove unimplemented features outright; restate differently-implemented features; never leave correction notes, deviation sections, or deferred markers in the artifacts.
- Update only `openspec/changes/<name>/`.
- Do not edit `openspec/specs/`, sync specs, or archive.
- Do not make application code changes unless explicitly asked after grounding reveals an issue.
- Preserve unrelated user changes.
- Prefer minimal edits, but rewrite as broadly as needed to keep the spec clairvoyant and internally consistent — never leave a stale or contradictory artifact just to avoid a larger rewrite.
