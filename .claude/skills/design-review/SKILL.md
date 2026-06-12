---
name: design-review
description: >-
  Software design & architecture review tailored to Arius. Reviews a diff, feature slice, file, or PR
  against clean-code, OO-boundary, model-driven/DDD, SOLID/DRY/YAGNI, design-pattern, component-architecture,
  and DDD enterprise-pattern lenses (Khalil Stemmler's software design map). Recognizes design patterns that
  are present but unnamed, and proposes pattern-based refactors. Use for design and architecture *quality* —
  it complements /code-review (correctness bugs) and the ArchUnit + Sonar/threading checks (mechanical rules).
  Invoke with an optional scope (path or feature) and --effort low|medium|high|max.
---

# Arius Design Review

A design-and-architecture review, calibrated to **how Arius is actually built**. It is organized around
Khalil Stemmler's [Software Design & Architecture map](https://khalilstemmler.com/articles/software-design-architecture/full-stack-software-design/),
scoped to the lenses that matter for this codebase.

## What this is for (and what it is not)

Arius is, by the README's own statement, an **agentic-engineering rewrite**: the code is largely
AI-generated, "important business logic may get glanced at," and **human attention goes to the tests**.
That makes design quality the thing most likely to drift unnoticed — so this review carries the load a
human architect would, looking for the failures that tests and analyzers do *not* catch.

This skill is **complementary**, not a duplicate:

| Concern | Owned by |
|---|---|
| Correctness bugs, reuse/efficiency nits | `/code-review` |
| Dependency rule, modulith, facade & internal-visibility boundaries | `Arius.Architecture.Tests` (ArchUnit) |
| Mechanical smells, security, threading | SonarAnalyzer.CSharp, SecurityCodeScan, VS Threading analyzers (CI) |
| **Design & architecture quality** — modeling, boundaries, principles, patterns | **this skill** |

Do not re-report what the columns above already cover. The highest-value findings here are *judgment*
calls a machine can't make: a leaky abstraction that still compiles, an anemic model, a speculative
interface, a pattern reinvented inconsistently across slices.

## How to run a review

1. **Determine scope** from the invocation:
   - No arg → review the branch diff: `git diff master...HEAD` plus uncommitted `git diff`.
   - A path or feature name (e.g. `src/Arius.Core/Features/RestoreCommand`) → review that slice.
   - A PR number/URL → fetch it with `gh pr diff <n>`.
2. **Read for context before you judge.** A finding is only credible if you've seen the conventions it
   supposedly violates. For the code under review, also read:
   - the **whole slice** (Command/Query + Handler + Models + Events), not just the changed lines;
   - the **shared services and value objects** it touches (`Shared/*`);
   - the relevant **ADRs** in `docs/decisions/` — these are *deliberate decisions*. Never fault an ADR'd
     choice; cite it. (Design specs live in `docs/superpowers/specs/`; architecture notes in
     `docs/filetrees.md`, `docs/cache.md`, `docs/commands.md`.)
3. **Know what's already enforced** (the table above) so you don't add noise.
4. **Apply the lenses** below at the chosen effort.
5. **Verify each candidate finding** before reporting: is it already covered by an arch test / analyzer? is
   it a deliberate ADR'd decision? is it actually consistent with an existing exemplar? Drop it if so.
6. **Report** in the output format at the end. Fixes are **fix-forward** (this project never reverts) and
   should match the nearest existing exemplar. Apply fixes only if the user asked (e.g. `--fix`).

**Effort scaling:**
- `low` / `medium` — only high-confidence, high-severity design findings (broken boundary, leaked detail,
  anemic core model, speculative abstraction). Few, sharp.
- `high` — broader: add Major + notable Minor findings; apply every selected lens deliberately.
- `max` — exhaustive across all lenses; include uncertain/speculative suggestions, clearly marked as such.
  For a large diff, fan out (e.g. an `Explore` agent or parallel readers per slice) then synthesize.

> The anchors below are accurate as of writing. The tree is AI-generated and evolves — **verify against the
> current code and prefer the actual exemplar** over this list when they disagree.

---

## Arius design language — judge against THIS, not generic ideals

Calibrate every finding to these conventions. Code that follows them is *correct for this codebase*, even
where a textbook might say otherwise.

- **Vertical slices.** One folder per use case under `Features/<Name>/`, each holding its `Command`/`Query`,
  `Handler`, `Models`, and `Events`. Slice-local types stay slice-local (enforced for the archive
  `BinaryFile`/`PointerFile`/`FilePair` models). Some cross-slice duplication is *deliberate* — see DRY below.
- **Source-generated Mediator CQRS.** Commands implement `ICommand<TResult>` with an `ICommandHandler<,>`
  (`ArchiveCommand→ArchiveResult`, `RestoreCommand→RestoreResult`, `RepairChunkIndexCommand→…`). Reads are
  **streaming queries** — `IStreamQueryHandler<,>` returning `IAsyncEnumerable` (`ListQuery`,
  `ContainerNamesQuery`, `ChunkHydrationStatusQuery`). **Handlers live in Core only** and Core is reached
  *primarily through Mediator* (ADR-0010). `AddMediator()` is intentionally called by the **outer** assembly
  (CLI / test host), never in Core's `AddArius`.
- **Shared kernel behind interfaces.** `Shared/*` services expose `IChunkStorageService`,
  `IChunkIndexService`, `IFileTreeService`, `ISnapshotService`, `IEncryptionService`. Feature handlers
  **orchestrate these services**; they should not reach past them to raw blob abstractions
  (`IBlobService`, `IBlobContainerService`, `IBlobServiceFactory`) except at a genuine blob-level boundary.
- **Ports & adapters (hexagonal).** Core *defines* the storage ports (`Shared/Storage/IBlob*`); `Arius.AzureBlob`
  *adapts* them. **Core stays Azure-free**, CLI stays Azure-free (all Azure goes through `Arius.AzureBlob`).
- **Value objects.** Typed hashes (`ContentHash`, `ChunkHash`, `FileTreeHash`) and path types (`RelativePath`,
  `PathSegment`) are the modeling backbone. The recipe: `readonly record struct`, **private** validated
  backing value, factory entry points (`Parse` / `TryParse` / `FromDigest` / `FromPlatformRelativePath`),
  a `default`-guard that throws "uninitialized", explicit conversions between related types, and domain
  operators (`RelativePath / PathSegment`). See ADR-0003 (distinct typed hashes), ADR-0004 (split filetree
  entry hash identities), ADR-0008 (internal filesystem domain types).
- **Modulith.** Namespace = module. An `internal` type may be used only within its **namespace subtree**;
  cross-namespace sharing must be opted into with `[SharedWithinAssembly]`. Implementation detail hides
  behind a **facade** (`IChunkIndexService` over the internal `ChunkIndexService` / `ChunkIndexLocalStore` /
  `ChunkIndexRouter`); even the persistence tech is hidden — *only* `ChunkIndexLocalStore` may know SQLite.
- **Composition root.** `ServiceCollectionExtensions.AddArius` wires implementations, using **explicit
  factories** where per-repository `accountName`/`containerName` can't be auto-injected.
- **Null Object / Strategy.** Encryption is a strategy chosen at startup: `PassphraseEncryptionService`
  (AES-256-CBC) vs the `PlaintextPassthroughService` no-op. `NullBlobServiceFactory`/`NullBlobService` are
  null objects for the no-storage path.
- **Result-as-DTO, not a Result monad.** Commands return result records carrying `Success` + optional
  `ErrorMessage` (e.g. `ArchiveResult`). That is the house style — **don't push for a `Result<T>` monad.**
- **Modern C#.** file-scoped namespaces, records & `readonly record struct`, primary constructors, the
  `field` keyword, collection expressions `[]`, `required`/`init`, nullable enabled,
  `ArgumentException.ThrowIfNullOrEmpty` guards. These are idiomatic here — never flag them as defects.

### Prioritize these AI-codegen failure modes

Given how this code is produced, weight the review toward the three things LLM-written code gets wrong most:

1. **Speculative generality** — an interface with a single non-boundary implementation, an unused
   `Options` field, a generic abstraction "for later." (YAGNI; Stage 4.)
2. **Missed reuse** — reinventing a helper, a guard, or worst of all a *value object* that already exists
   (`RelativePath`, the typed hashes). (DRY done right; Stages 1 & 9.)
3. **Cross-slice inconsistency** — one slice modeling, validating, logging, or publishing events differently
   from its siblings. Point to the canonical slice. (Stages 4 & 5.)

---

## Review lenses

Each lens is one stage of the Stemmler map, **scoped as requested**. Stages 2 and 3 are deliberately
narrowed; Stage 7 (Architectural Styles) is out of scope for this skill.

### Stage 1 — Clean Code *(full)*
Intent over cleverness. Look for:
- **Naming & ubiquitous language** — do identifiers use the domain's words (chunk, snapshot, filetree, tier,
  rehydrate, thin/large/tar, shard)? Misleading or generic names (`data`, `Process`, `Manager`) in
  business code.
- **Function focus** — one level of abstraction per method; a handler step that mixes orchestration with
  byte-twiddling wants extracting. Over-long methods that hide phases.
- **Primitive obsession** — a raw `string`/`int` standing in for a hash, a path, a tier, a size threshold.
  Almost always should be one of the existing value objects (see Stage 9).
- **Magic values** — literals like `1 MB` / `64 MB` thresholds; are they named options (as in
  `ArchiveCommandOptions`) or inline constants drifting between slices?
- **Nulls** — nullable is enabled; prefer guard-throws and the `default`-guard struct idiom over silent
  nulls. Flag nullable creeping into a domain value's public surface.
- **Comments** — should explain *why* (like the ADR-referencing comments in the arch tests), not restate
  the code. Flag stale or narrating comments.
- **Dead code / unused options** — especially AI-left scaffolding.

*Skip what Sonar already flags mechanically* (formatting, obvious smells). Focus on intent and abstraction
level, which analyzers miss.

### Stage 2 — Programming Paradigms → **Object-Oriented only**
Use OO to define **architectural boundaries**, not just to hold data:
- **Polymorphism over type-codes** — a `switch`/`if` on a "kind" (chunk type, tier, encrypted-vs-plain) that
  recurs in multiple places is a missing polymorphic abstraction. Arius already does this well: encryption
  is a strategy, blob types are distinguished at the storage edge.
- **Program to the boundary abstraction** — cross-boundary calls should go through the port/service interface
  (`IBlob*`, `I*Service`), and **plug-ins point inward**: `Arius.AzureBlob` depends on Core's abstractions,
  never the reverse.
- **Leaked concretions** — a concrete implementation type (or its tech: `Azure.*`, `Microsoft.Data.Sqlite`,
  a concrete `*Service`) surfacing in a signature, return type, or another module's code.

### Stage 3 — Object-Oriented Programming → **Model-Driven Design only**
Does the code grow a **rich domain model**, or push logic into procedural handlers around dumb data?
- **Anemic model smell** — validation/derivation/invariants for a domain concept living in a handler or
  service instead of *with the data*. Compare to `RelativePath`/`PathSegment`, which own their own
  validation, composition (`/`), and segment logic.
- **Make illegal states unrepresentable** — a concept that can be constructed invalid, then validated later,
  should validate **on construction** (the `Parse`/`TryParse` + private-ctor recipe).
- **Implicit concept that wants a type** — a recurring tuple, an "it's a string but really a …", a rule
  duplicated across slices. ADR-0008 is exactly this move (filesystem strings → domain types); apply the
  same judgment to new code.
- **Behavior near data** — methods that operate on a value object's internals belong on the value object.

### Stage 4 — Design Principles *(full)*
- **SOLID — SRP first.** Does each handler do **one use case**? Does each `Shared` service have one reason to
  change (`ChunkStorageService` = chunk blobs, `ChunkIndexService` = dedup index, …)? A handler that has
  grown several unrelated responsibilities should split.
- **OCP / DIP** — extension via new strategies/adapters rather than editing a switch; depend on
  `I*Service`/ports, not concretions (already strong — flag regressions).
- **Composition over inheritance** — the codebase is composition-first (records, structs, interfaces, little
  inheritance). **Flag any new inheritance hierarchy** in domain/feature code; prefer composition or a
  strategy.
- **Encapsulate what varies** — the encryption strategy is the model; new axes of variation (tier behavior,
  storage backend) should be encapsulated the same way, not branched inline.
- **Hollywood / IoC** — wiring belongs in the composition root and is invoked via DI, Mediator dispatch, and
  progress callbacks — not via service-locator reach-ins.
- **DRY — but respect slice autonomy.** Genuine duplication of a *rule* or a *value* is a defect (extract to
  the model or `Shared`). Incidental similarity between two slices is **not** — vertical slices may diverge,
  and over-DRYing them creates the wrong coupling. Distinguish the two explicitly.
- **YAGNI** — the top AI-codegen smell. Flag single-impl interfaces that aren't boundaries, unused options,
  "framework" abstractions with one caller, and configurability nobody asked for.

### Stage 5 — Design Patterns → **recognize unnamed, and suggest refactors**
Two modes — do **both**:

**(a) Recognize patterns that are present but unnamed**, then verify they honor the pattern's contract and are
applied *consistently*. Known instances to recognize and check:

| Pattern | Where it lives in Arius |
|---|---|
| Strategy | `IEncryptionService` → passphrase vs plaintext |
| Null Object | `PlaintextPassthroughService`, `NullBlobService`/`NullBlobServiceFactory` |
| Facade | `IChunkIndexService` hiding store/router/SQLite internals |
| Adapter | `Arius.AzureBlob` adapting Core's `IBlob*` ports |
| Factory | `IBlobServiceFactory`; the `FromDigest`/`Parse` value-object factories |
| Mediator / Observer | Mediator commands/queries; `INotification` progress events → CLI subscribers |
| Builder | `TarBuilder`, `FileTreeBuilder`, `FileTreeStagingWriter` |
| Pipeline / staged producer-consumer | the archive hash→bundle→upload→snapshot stages |

If a new slice reinvents one of these inconsistently (e.g. its own ad-hoc null check instead of the null
object, or a bespoke facade leak), **name the pattern and point to the canonical instance.**

**(b) Suggest a pattern-based refactor** when code has a smell a pattern resolves:
- repeated `switch` on a kind → **Strategy / polymorphism**;
- scattered `if (x == null)` defenses → **Null Object**;
- multi-step object construction inline → **Builder / Factory**;
- duplicated cross-cutting orchestration across slices → **Template Method** or the existing **pipeline** shape;
- an interface that doesn't fit a caller → **Adapter**.

Always propose the refactor **in Arius's idiom**, referencing the exemplar. **Never** recommend a pattern for
its own sake — a single-use abstraction loses to YAGNI (Stage 4).

### Stage 6 — Architectural Principles *(full)*
- **Stable Dependencies** — dependencies point toward the stable core. Core is stable; adapters/CLI are
  volatile and depend inward. **Flag any inward→outward dependency** (Core or a Shared service reaching out
  to an adapter, a feature, or a tech detail).
- **Stable Abstractions** — the stable Core is abstract (interfaces/ports/value objects). A stable type that
  is also concrete and hard to extend is a smell.
- **Acyclic Dependencies** — no cycles. The modulith test catches *namespace* leaks, but **not** design-level
  cycles or those hidden in lambdas/async state machines. Watch for **feature↔feature** coupling and a
  `Shared` service depending back on a feature.
- **Policy vs Detail** — business policy (dedup, snapshotting, tiering rules, tree structure) lives in Core;
  details (SQLite, Azure, tar/gzip, blob layout) live at the edges. **Flag detail drifting toward policy** —
  e.g. SQLite or `Azure.*` types creeping inward, or blob-naming/layout knowledge leaking into a handler that
  should go through a `Shared` service.
- **Boundaries & subdomains** — for a new concept, ask the placement question: does it belong *in this slice*,
  in `Shared`, or is it a new module? Getting this wrong is how slices start leaking into each other.

> The ArchUnit suite is the automated backstop for several of these — but it explicitly does **not** see
> usages inside lambdas / async state machines, and can't reason about design-level cycles or placement.
> That gap is exactly where this lens earns its keep. If you find a violation the tests *should* catch but
> can't, flag it **and** note the test blind spot.

### Stage 8 — Architectural Patterns *(full)*
- **DDD + layered** — the layering is CLI/composition → Mediator use-case handlers → domain/`Shared` services
  → ports → adapters. Check the change respects it: no layer-skipping, no upward calls.
- **MVC** — not used by Core. The `Arius.Explorer` app is a separate UI; only consider MVC/MVVM concerns if
  the review scope is Explorer.
- **Event Sourcing — recognize what Arius is and isn't.** Arius is **not** event-sourced. Its **snapshots**
  are an immutable, append-only point-in-time record from which state (the Merkle filetree) is reconstructed,
  and its `INotification`s are **progress/notification** events, *not* persisted domain events. So: don't
  mistake the notifications for an event store, and **don't suggest introducing event sourcing.** The valuable
  check is **invariant preservation** — snapshots are immutable and never deleted (README; ADR-0002 governs
  no-op archive behavior). Does the change keep snapshots/filetrees/chunks append-only and consistent?

### Stage 9 — Enterprise Patterns *(full)*
- **Entities vs Value Objects.** Arius is **value-object-heavy** and **content-addressed**: identity *is* the
  content hash. Be suspicious of a new mutable, identity-bearing **entity** introduced casually — usually the
  right model here is an immutable value or a content-addressed record.
- **Value Objects.** A new domain value must follow the established recipe (immutable `readonly record struct`,
  validate-on-construct, factories, `default`-guard, no raw value leaking onto the public surface, conversions
  via explicit factory). Re-using a raw `string`/`int` where a VO exists is the failure mode (ties back to
  Stages 1 & 3).
- **Domain Events.** `INotification` records, named for the moment they signal (`FileHashedEvent`,
  `ChunkUploadedEvent`, `SnapshotCreatedEvent`, …), immutable, carrying data not behavior, **published by the
  handler at the right pipeline point** and handled in the CLI for progress. New events should match this
  shape and naming, and not become a backdoor for cross-slice control flow.
- **Repository / persistence services.** The `Shared` services (`SnapshotService`, `FileTreeService`,
  `ChunkIndexService`, `ChunkStorageService`) are Arius's repository-like persistence boundary. New
  persistence should go **through or alongside** them, not directly against `IBlob*`. Repair/rebuild paths
  should stay **idempotent and re-runnable** (as the chunk-index repair is designed to be).
- **Aggregates / consistency boundaries.** A snapshot + its filetree + referenced chunks + the dedup index
  form a consistency story. Check the change keeps them coherent (e.g. every content-hash still has a chunk
  blob; thin chunks still point at a live tar; the index can still be rebuilt from committed chunks).
- **Specifications** are not used — don't introduce them. **Bounded contexts** map to the assemblies
  (`Arius.Core`, `Arius.AzureBlob`, `Arius.Cli`, `Arius.Explorer`); respect their seams.

---

## What NOT to flag (signal discipline)

- Anything the **ArchUnit tests** already enforce (dependency rule, modulith, facade boundaries, internal
  visibility, SQLite isolation) — *unless* the change breaks a rule in a way the tests can't see
  (lambdas/async/design cycles), in which case flag it **and** note the gap.
- Anything **Sonar / SecurityCodeScan / threading analyzers** catch mechanically.
- **Deliberate ADR'd decisions** — typed hashes (0003), filetree hash split (0004), Stryker scope (0005),
  staged filetree build (0006), phase/detail logging (0007), filesystem domain types (0008), feature handlers
  as use cases (0010), 90% coverage (0011), no-op archive snapshot behavior (0002). Cite them; don't fault them.
- **House styles**: result DTOs (not a `Result<T>` monad), deliberate cross-slice duplication,
  `default`-guarded structs, `AddMediator()` in the outer assembly, modern-C# idioms.
- **Correctness bugs** — defer to `/code-review`. (If a *design* flaw causes a bug, mention both and link.)
- **Test-code quality** — that's the human's domain and `/code-review`'s; review production design here.
  You *may* note when a test encodes or reveals a design smell.

---

## Output format

Group findings by severity, highest first. For each:

```
[Severity] <one-line title>  ·  file/path.cs:line  ·  Stage N — <lens>
  What:  one or two sentences on the design issue.
  Why:   why it matters in Arius specifically.
  Fix:   concrete fix-forward step, referencing the canonical exemplar
         (e.g. "model as a value object like RelativePath", "go through IChunkIndexService").
```

**Severity rubric:**
- **Critical** — broken boundary / dependency rule, detail leaked into policy, or a violated
  immutability/consistency invariant (snapshots, dedup index, content-addressing).
- **Major** — anemic core model, primitive obsession in the domain, an SRP-violating handler, a speculative
  abstraction (YAGNI), a pattern reinvented inconsistently.
- **Minor** — naming / abstraction-level / readability issues analyzers miss; small modeling improvements.
- **Suggestion** — pattern naming, optional refactors, "consider" notes (the bulk of `max`-effort output).

End with a short **design-health summary** (2–4 lines): is the change consistent with Arius's architecture,
and what is the single most important thing to address. If a finding reflects a genuinely new design
decision, note that it may warrant **a new ADR in `docs/decisions/`**.
