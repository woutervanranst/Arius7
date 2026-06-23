---
description: Sync the docs/ tree to a finished change (diff + intent), preferring tables/mermaid/bullets over prose
---

Sync the `docs/` tree to a finished change.

**Input** (all optional): `--base <ref>` (default `master`), `--intent <path>` to the planning artifact, `--effort low|medium|high`. Example: `/update-docs --base master --effort high`.

Use the repo-local `update-docs` skill for the full procedure (map changed code → affected docs, classify by doc type, apply grounded edits, freeze the intent into `docs/history/`, run the `mkdocs build --strict` gate). Read `docs/README.md` first — it is the law for *what goes where*.

Makes NO code changes.