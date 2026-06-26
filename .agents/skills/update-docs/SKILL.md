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
- **Carry intent, not the code's "what":** a doc edit should state the *invariant, structure, or why* and name the type/method so a reader can jump to it — never re-narrate the parsing rules, branch conditions, or field-by-field mechanics the code (and its `// ──` docstrings) already own. If a sentence would go stale the moment a line of code changes, it belongs in code, not the doc. Cite `Type.Method` / a flag name as the anchor and stop; one tight clause beats a paragraph that re-derives the implementation. Smell test for each edit: would this still read correctly after a mechanical refactor that preserves behaviour? If not, cut it back to the intent.
- **Prefer structure over prose:** reach for a **table**, a **mermaid diagram**, or a tight **bullet list** before writing a paragraph. Tables for anything comparative or enumerable (per-host/per-variant differences, option/field/flag/event matrices, "X vs Y"); mermaid for flows, sequences, and state; bullets for invariants and short enumerations. Reserve prose for the *"why"* that doesn't fit a row or an arrow. A paragraph that restates a comparison or a list is a smell — convert it. Don't pad: a 3-row table beats three sentences, but a single fact is just a sentence.

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
- CLI options / API endpoints / UI features → the relevant `docs/guide/*`; how you build/run/test locally →
  `docs/guide/development.md`.
- a cross-cutting change (events/progress, logging, performance, memory-boundedness, service-lifetimes,
  testing) → `docs/design/cross-cutting/*`.
- a **new** design doc or guide → create it at the mirrored path (follow the template in `docs/README.md`)
  **and add a `nav:` entry in `mkdocs.yml`** — the site builds `--strict`, so a page missing from nav fails
  it. A **deleted/renamed** unit → remove or repoint its doc, its `mkdocs.yml` nav entry, and any links to
  it; if you're collapsing one doc into another, **verify the lift was faithful** (step 4).

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
  names. Match the surrounding doc's voice, and favour a **table / mermaid / bullets** over a paragraph
  wherever the content is comparative, enumerable, or a flow (see Principles) — prose is the fallback, not
  the default.
- Touch only what the change actually affects (surgical edits, like `simplify`/`design-review`).
- **Links that leave `docs/`** — to the repo-root `README.md`, `docker-compose.yml`, or `src/**` — must be
  **absolute GitHub URLs** (`https://github.com/woutervanranst/Arius7/...`), never relative: a relative link
  that escapes `docs/` breaks the `--strict` site build. In-tree `.md` links stay relative.
- **When deleting or collapsing existing doc content** (not just a planning artifact), verify the lift is
  faithful: diff the old content against where it now lives — recover it with `git show <base>:<path>` if it
  was already removed — and restore any dropped **durable** content (intent, invariants, scannable reference
  tables). Do **not** restore mechanical detail the code owns — that re-creates the over-documentation MECE avoids.
- For a large diff, spawn one subagent per affected doc to draft in parallel, then do a single consistency
  pass (voice, cross-links, no overlap between docs).

### 5. Freeze the intent artifact into history
- Move/copy the planning artifact (PLAN / CONVO / spec **markdown**; drop raw `.txt` transcripts, binaries,
  UI mockups) into `docs/history/agentic-plans/<YYYY-MM-DD>-<slug>/`, and add one line to
  `docs/history/INDEX.md` noting where its durable intent landed (the ADR/design doc). This preserves the
  "why" without polluting the living docs. (Use the change/PR date for `<YYYY-MM-DD>`.)

### 6. Verify and report
Run the gates and fix anything they flag:
- **Site build (the authoritative gate):** `pip install -r docs/requirements.txt && mkdocs build --strict`
  exits 0 with **no warnings**. `--strict` validates every relative link, every `#anchor`, and that new
  pages are in `mkdocs.yml` nav — it supersedes a manual link grep. It is the same gate CI runs
  (`.github/workflows/docs.yml`). Common fix: a cross-doc heading link must match MkDocs' slug (e.g.
  `## Open seams / future` → `#open-seams-future`). (`docs/history/` is `not_in_nav` and may keep frozen
  absolute paths.)
- **No stale references:** nothing in the living docs (or code comments) points at a path this change moved
  or deleted — `grep -rn '<moved-or-removed-path>' docs src --include='*.md' --include='*.cs'`.
- **Isomorphism / coverage:** every new or changed `src/` unit maps to a doc, or is deliberately code-only
  (state which, and why).
- **Build sanity:** `dotnet build` stays green — a docs-only change must not have touched code; if you
  edited a code comment (e.g. a doc-path reference), confirm it still builds.

Finish with a short summary: which docs changed and why, any new ADR proposed (and the decision it records),
the frozen artifact location, and the gate results.
