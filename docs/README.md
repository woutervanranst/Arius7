# Arius documentation

This tree is **MECE** (every concept has exactly one home) and **mirrors the code** (`docs/design/` parallels `src/`), so if you know where something lives in the code you know where its doc lives.

## The four doc types

| Type | Lives in | Answers | Lifecycle |
|---|---|---|---|
| **ADR** | [`decisions/`](decisions/) | "Why did we choose X over Y, once" — one decision, its alternatives, its consequences | Immutable once accepted; you *supersede* an ADR, you never edit it |
| **Design doc** | [`design/`](design/) (mirrors `src/`) | "How does this slice/component work, and why is it shaped this way" — structure, invariants, open seams | **Living** — kept in sync with the code |
| **Guide** | [`guide/`](guide/) | "How do I use or deploy it" — task-oriented user/operator instructions | Living |
| **History** | [`history/`](history/) | The reasoning path that produced the above (openspec, superpowers, agentic plans) | **Frozen, read-only** — never maintained |

Plus one reference: [`glossary.md`](glossary.md) — the grounded domain vocabulary. Every term links to where it is defined in code. Design docs link terms here instead of redefining them.

## Where things go (the rules)

- **Mechanical "what" + local intent → stays in code.** Method contracts, control flow, and the numbered `// ── Stage N ──` handler-docstring idiom are the in-code documentation. Do **not** restate them in prose — if a sentence goes stale when a line of code changes, it belongs in code (or nowhere).
- **Cross-file "how it fits + invariants + why this shape + open seams" → a design doc** under `design/`, in the location that mirrors the code.
- **A one-time, cross-cutting decision (with alternatives) → an ADR** under `decisions/`.
- **A user or operator task → a guide** under `guide/`.
- **Each concept gets exactly one home.** E.g. the chunk-index sharding algorithm lives in `design/core/shared/chunk-index.md` (the *how*) + `decisions/adr-0015-*` (the *decision*), and nowhere else.
- **A design doc exists only when there is intent above the code.** Trivial folders (e.g. `Shared/Extensions`) and one-file queries get no doc, or a grouped one — never a stub.

## Layout (mirrors `src/`)

```
decisions/                ADRs (MADR, flat, numbered)
glossary.md               domain vocabulary → defining type/file
design/
  README.md               architecture overview (vertical slicing, Core⊥hosts, event flow, service stack)
  core/
    features/             mirrors src/Arius.Core/Features  (one per command/query slice)
    shared/               mirrors src/Arius.Core/Shared     (one per component with intent)
  hosts/                  cli · explorer · web (Arius.Web + Arius.Api, one container)
  cross-cutting/          events-and-progress · logging · performance · memory-boundedness · testing
guide/                    cli · web-ui · explorer · deployment
history/                  openspec-archive · superpowers · agentic-plans  (frozen) + INDEX.md
```

## Design-doc template

Every file under `design/` follows this shape:

```markdown
# <Component or slice name>

> **Code:** `src/Arius.Core/...`  ·  **Decisions:** [ADR-00NN](../../decisions/adr-00NN-...)  ·  **Terms:** [chunk](../../glossary.md#chunk)

## Purpose
What this unit is responsible for, in 1–3 sentences.

## How it works
The structure and flow. Use a mermaid flowchart or sequence diagram when a picture
explains it better than prose — grounded in real type/method names.

## Key invariants
The load-bearing rules that must hold (the things a refactor must not break).

## Why this shape
The design rationale. Link an ADR for each one-time decision rather than restating it.

## Open seams / future
Known limitations and where the next change would go.
```

Keep claims grounded: cite the real type/method (e.g. `ChunkIndexService.FlushAsync`) so a reader can jump to the code.

## Adding or changing a doc

1. Decide the **type** (table above) — that picks the folder.
2. For a design doc, put it at the path that **mirrors the code**.
3. Reuse glossary terms; link ADRs for decisions; don't duplicate another doc's concern.
4. If you made a one-time architectural choice, also add an ADR (copy [`decisions/adr-template.md`](decisions/adr-template.md)).
