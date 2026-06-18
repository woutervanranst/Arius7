---
name: update-docs
description: >-
  Sync the docs/ tree to a finished change. Use when a PR/change is ready and its documentation needs
  updating: reads the diff plus the change's planning artifact (an agent conversation, a superpowers-style
  spec, or a PLAN.md), then updates only the affected ADRs / design docs / guides / glossary, freezes the
  planning artifact into docs/history/, and runs the verification gates. Makes NO code changes — it is the
  doc-sync sibling of code-review (bugs) and design-review (design quality). Args: --base <ref> (default
  master), --intent <path>, --effort low|medium|high.
invocable: true
---

# Update Arius docs

Keep the `docs/` tree in sync with a finished development, driven by the diff and the change's intent
artifact. The documentation system is **MECE and mirrors `src/`**; the rules that govern *what goes where*
already live in **`docs/README.md`** ("The four doc types", "Where things go", the design-doc template) and
**`docs/glossary.md`**. **Read `docs/README.md` first** and follow it — this skill is the *procedure*, that
file is the *law*. Do not restate those rules here or in the docs.

## When to use

- A change is implemented (working tree or a PR branch) and the docs must reflect it.
- Invoke after the code is settled, typically when the PR is ready.

## When NOT to use

- To review code (use `code-review` / `design-review`). This skill never edits code.
- For mechanical "what the code does" prose — the numbered `// ── Stage N ──` handler docstrings own that.
  Docs carry intent, structure, invariants, and "why", at a level that survives line-edits.

## Principles (from `docs/README.md`)

- **Four homes, mutually exclusive:** a one-time decision → an **ADR** (`docs/decisions/`); a subsystem's
  how/shape/invariants → a **design doc** (`docs/design/`, path **mirrors `src/`**); a user/operator task →
  a **guide** (`docs/guide/`); vocabulary → the **glossary**. Archaeology is frozen in `docs/history/`.
- **Mirror the code:** `docs/design/` parallels `src/` — `core/features/`, `core/shared/`, `hosts/`,
  `cross-cutting/`. A unit gets a design doc only if it has intent above the code; trivial folders stay
  code-only (don't create stubs).
- **Don't duplicate:** link the glossary for terms and the ADR for decisions instead of restating them.

## Procedure

### 1. Gather the change and its intent
- Diff: `git diff <base>...HEAD` (default `--base master`) limited to `src/`, `README.md`, and infra
  (`Dockerfile`, `docker-compose.yml`, `src/Arius.Web/**`). Note added / changed / **deleted** paths.
- Intent artifact (the "why" the diff can't show): use `--intent <path>` if given; otherwise look for it —
  the PR description, an uncommitted notes file, or a `*.md` plan/spec added in the diff. If none is found
  and the change has non-obvious rationale, **ask the user** to point at it before proceeding.

### 2. Map changed code → affected docs (mirror the path)
- `src/Arius.Core/Features/<X>/` → `docs/design/core/features/<x>.md` (tiny queries share `queries.md`).
- `src/Arius.Core/Shared/<X>/` → `docs/design/core/shared/<x>.md`.
- a host project (`Arius.Cli` / `Arius.Api` / `Arius.Web` / `Arius.Explorer`) → `docs/design/hosts/<h>.md`,
  and if user-visible behavior changed, the matching `docs/guide/<h>.md`.
- CLI options / API endpoints / UI features → the relevant `docs/guide/*`.
- a cross-cutting change (events/progress, logging, performance, memory-boundedness, service-lifetimes,
  testing) → `docs/design/cross-cutting/*`.
- a **new** `src/` unit with intent above the code → a **new** design doc at the mirrored path (follow the
  template in `docs/README.md`). A **deleted** unit → remove or repoint its doc and any links to it.

### 3. Classify each update by doc type (and keep it MECE)
- A genuine **one-time architectural decision** (a choice with alternatives, evident in the diff/intent) →
  propose a **new ADR**: next number, copy `docs/decisions/adr-template.md`, fill it from the diff and the
  intent's rejected alternatives, then have the relevant design doc **link** it. Don't invent decisions
  that weren't made.
- A change to a subsystem's **shape, invariants, or open seams** → edit that **design doc** (update its
  mermaid too); link the ADR rather than restating the rationale.
- A new/renamed **domain term** → the **glossary** (term → one-line intent → defining type/file).
- New/changed **user or operator behavior** → the **guide**.

### 4. Apply grounded edits (to the working tree)
- Ground every claim in the **post-change** code: read the changed files; cite real type/method/constant
  names. Match the surrounding doc's voice and use mermaid where a picture beats prose.
- Touch only what the change actually affects (surgical edits, like `simplify`/`design-review`).
- For a large diff, spawn one subagent per affected doc to draft in parallel, then do a single consistency
  pass (voice, cross-links, no overlap between docs).

### 5. Freeze the intent artifact into history
- Move/copy the planning artifact (PLAN / CONVO / spec **markdown**; drop raw `.txt` transcripts, binaries,
  UI mockups) into `docs/history/agentic-plans/<YYYY-MM-DD>-<slug>/`, and add one line to
  `docs/history/INDEX.md` noting where its durable intent landed (the ADR/design doc). This preserves the
  "why" without polluting the living docs. (Use the change/PR date for `<YYYY-MM-DD>`.)

### 6. Verify and report
Run the same gates the consolidation used and fix anything they flag:
- **Links:** every relative `.md` link under `docs/` resolves (exclude fenced code blocks; `docs/history/`
  may keep its frozen absolute paths).
- **No stale references:** nothing in the living docs (or code comments) points at a path this change moved
  or deleted — `grep -rn '<moved-or-removed-path>' docs src --include='*.md' --include='*.cs'`.
- **Isomorphism / coverage:** every new or changed `src/` unit maps to a doc, or is deliberately code-only
  (state which, and why).
- **Build sanity:** `dotnet build` stays green — a docs-only change must not have touched code; if you
  edited a code comment (e.g. a doc-path reference), confirm it still builds.

Finish with a short summary: which docs changed and why, any new ADR proposed (and the decision it records),
the frozen artifact location, and the gate results.
