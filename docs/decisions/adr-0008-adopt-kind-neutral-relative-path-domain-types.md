---
status: accepted
date: 2026-05-04
decision-makers: Wouter Van Ranst, OpenCode
---

# Introduce Strongly Typed Repository And Local Root Path Types

## Context and Problem Statement

Arius already treats archived paths as a canonical repository-internal form: forward-slash separated, relative to the archive root, and independent from local operating-system path syntax. However, much of that domain has historically moved through the codebase as raw `string` values. Validation, normalization, joining, root handling, and display rules were therefore scattered across archive, list, restore, filetree, fixtures, and E2E dataset code.

The architectural question is how Arius should represent repository-internal paths and rooted local filesystem paths so those invariants live in strong types instead of in repeated string helpers, while still keeping host filesystem behavior at explicit boundaries.

## Decision Drivers

* repository-internal paths are a core Arius domain concept, not incidental formatting
* Arius should prefer strong types over primitives as early and as richly as possible
* archive, list, restore, and filetree workflows should operate on canonical repository paths as typed values
* the same logical path should not need repeated normalization and validation throughout the codebase
* local operating-system roots and rooted local paths are also first-class Arius concepts and should not remain raw strings
* local operating-system paths and repository-internal paths have different semantics and should not be conflated
* repository path identity must remain stable across machines and must not inherit host filesystem comparison rules
* strings should remain only at true boundaries such as logs, current CLI or progress payloads, persisted wire formats, storage names, and host OS path joins not yet re-typed
* directory display conventions such as a trailing slash should not redefine the identity of the underlying path
* Arius often knows a repository path before it knows whether the node at that path is a file or directory

## Considered Options

* Keep string-based path helpers and continue centralizing validation in helper methods
* Introduce strongly typed repository path types with kind-neutral `PathSegment` and `RelativePath` only
* Introduce strongly typed repository path types plus explicit local-root types `LocalRootPath` and `RootedPath`
* Introduce a typed path hierarchy immediately, including `FilePath`, `DirectoryPath`, and specialized entry-name types

## Decision Outcome

Chosen option: "Introduce strongly typed repository path types plus explicit local-root types `LocalRootPath` and `RootedPath`", because it captures both Arius repository-path identity and local rooted-path boundaries directly, removes repeated stringly helpers, and aligns with the broader project rule to type owning state and APIs before adding conversion helpers.

`RelativePath` is the primary repository-internal path value object. It represents a canonical path inside the archived repository tree, not a host operating-system path. Root is represented explicitly as `RelativePath.Root`, not as a raw empty string at API boundaries.

`RelativePath` uses stable repository-path identity rules: it is case-preserving and uses ordinal, case-sensitive equality and hashing on every operating system. Arius must not inherit comparison behavior from the current host filesystem.

`PathSegment` is the primitive value object for one canonical segment. A segment is non-empty, not whitespace-only, contains no separators, rejects `.` and `..`, and can be used wherever a single child-name identity matters.

`LocalRootPath` is the primary local-root boundary value object. It represents an absolute canonical local filesystem anchor, does not require the target directory to exist, and owns root-relative conversion operations.

`RootedPath` represents a `RelativePath` anchored at a `LocalRootPath`. It is the local filesystem address counterpart to repository-relative identity and should be adopted broadly through archive, list, and restore where Arius works with repository-relative paths against a known local root.

The base repository-path model remains kind-neutral. File-vs-directory meaning stays on repository entries and filetree node metadata, not on `RelativePath`. A trailing slash remains serializer or display syntax for directory entries, not part of path identity. The leaf-name concept for both files and directories remains `PathSegment`; Arius does not introduce a separate `FileName` type in this decision.

Owning boundary APIs for repository-path and local-root conversion live in `Arius.Core.Shared.Paths`. Rooted local joining should move toward `LocalRootPath` and `RootedPath` ownership. `RelativePath` may expose `RootedAt(LocalRootPath root)` because that preserves the explicit composition of repository-relative identity plus local root, but rooted local-path formatting should not remain a generic `RelativePath` responsibility.

Equality and hashing semantics are intentionally split:

* `RelativePath` equality and hashing are repository-stable and OS-independent
* `LocalRootPath` and `RootedPath` equality and hashing follow host local-path semantics as closely as practical
* these local-path types are boundary types and must not be treated as cross-machine persistent identity

This means:

* on Windows, `C:\repo` and `c:/repo/` compare equal as `LocalRootPath`
* on Linux, `/repo` and `/Repo` compare different as `LocalRootPath`
* if two different `RelativePath` values root to the same Windows path, collapsing them under `RootedPath` host equality is useful because that collision is a real restore hazard

When moving code toward typed paths, Arius should prefer re-typing the owning state, channels, bags, intermediate records, and shared method signatures before adding helper methods that mainly convert typed paths back into strings. If a conversion helper is still needed, it belongs on the owning shared boundary or domain API, not inside feature handlers.

This decision does not require Arius to introduce `FilePath` and `DirectoryPath` immediately. Those may be added later if concrete APIs show a strong need for kind-specific compile-time constraints, but the base path model remains intentionally kind-neutral.

### Consequences

* Good, because archive, list, restore, filetree, and test infrastructure can share one canonical repository-path model plus one explicit local-root model instead of repeating validation and normalization logic.
* Good, because repository path equality and hashing stay stable across machines instead of drifting with host filesystem behavior.
* Good, because local rooted-path equality can follow practical host-path semantics where restore and archive collision behavior matters.
* Good, because root, parent/child composition, segment identity, repository-relative rooting, and local-root relativization now have an explicit home in `Arius.Core.Shared.Paths`.
* Good, because local disk paths and repository-internal paths remain clearly separated concepts instead of being conflated in raw strings.
* Good, because directory display rules such as `photos/` stay boundary-specific rather than leaking into the domain identity of `photos`.
* Good, because the preferred direction is to type state and APIs first, which avoids feature-local helper sprawl.
* Bad, because remaining string-based feature flows and tests still need to be moved onto `RelativePath`, `LocalRootPath`, and `RootedPath` over time.
* Bad, because host-path equality is only an approximation of OS and filesystem behavior, so exact filesystem constraints still need boundary validation where required.
* Bad, because some current query, event, and serializer surfaces still intentionally expose strings at boundaries, so narrow conversions remain necessary until those boundaries change.

### Confirmation

The decision is being followed when repository-internal paths in archive, list, restore, filetree, and related tests are represented as `RelativePath` rather than raw `string`, when local roots are represented as `LocalRootPath`, when rooted local filesystem addresses are represented as `RootedPath`, and when single path components are represented as `PathSegment` where segment identity matters.

The decision is not being followed when new code introduces fresh canonical-path validators, normalizers, or feature-local helpers over raw strings inside the repository domain. (TODO)

Code review should check that:

* canonical repository paths are parsed at boundaries and then carried as `RelativePath`
* `RelativePath` equality and hashing remain ordinal and case-sensitive regardless of host OS
* local roots are parsed at boundaries and then carried as `LocalRootPath`
* rooted local filesystem addresses use `RootedPath` or other owning `Arius.Core.Shared.Paths` APIs instead of feature-local slash replacement or join helpers
* filetree and listing code do not use trailing slash as path identity
* state, channels, intermediate records, and shared APIs are re-typed before introducing string conversion helpers
* new handler-local helpers such as `ToRelativePathText`, `GetLocalPath`, or `GetFileTreePath` are not added where the owning state or shared API should be typed instead

Tests should verify canonical parsing, root handling, path composition, parent and name semantics, case-sensitive repository equality, host-path local-root equality, rooted-path conversion behavior, and formatting behavior at serializer or UI boundaries.

## Pros and Cons of the Options

### Keep string-based path helpers and continue centralizing validation in helper methods

This keeps repository paths as strings and relies on helper methods to preserve invariants.

* Good, because it has the lowest immediate code churn.
* Good, because it can reduce the worst duplication quickly.
* Bad, because canonical repository paths still travel through the domain as raw strings.
* Bad, because helper sprawl tends to reappear whenever a new boundary or formatting rule is introduced.
* Bad, because root, segment, parent, and composition semantics remain conventions instead of types.

### Introduce strongly typed repository path types with kind-neutral `PathSegment` and `RelativePath` only

This would improve repository identity but leave local roots and rooted local filesystem addresses string-based.

* Good, because it models Arius repository-path identity directly.
* Good, because it separates repository-path identity from file-vs-directory interpretation and from display syntax.
* Good, because it fixes equality and hashing semantics to stable ordinal behavior across machines.
* Bad, because Arius still frequently operates on repository-relative paths against a known local root, so stringly rooted-path helpers would remain in archive, list, and restore.
* Bad, because rooted local joining and relativization would still lack a first-class owner.

### Introduce strongly typed repository path types plus explicit local-root types `LocalRootPath` and `RootedPath`

This is the chosen design.

* Good, because it models Arius repository-path identity directly.
* Good, because it also models local roots and rooted local filesystem addresses directly.
* Good, because it separates repository-path identity from file-vs-directory interpretation, display syntax, and host local-path semantics.
* Good, because it fixes repository equality and hashing semantics to stable ordinal behavior across machines while allowing practical host-path semantics for local rooted paths.
* Good, because it gives one central home for validation, composition, root semantics, repository-relative rooting, and root-relative local conversion.
* Good, because it aligns with the project-wide guidance to prefer strong types over primitives and to type owning state before adding adapters.
* Bad, because adoption still touches many core types, handlers, tests, and serializers.

### Introduce a typed path hierarchy immediately, including `FilePath`, `DirectoryPath`, and specialized entry-name types

This would extend the type system further from the start.

* Good, because some APIs could express stronger intent at compile time.
* Good, because directory-specific or file-specific operations could be made explicit.
* Bad, because Arius often knows a path before it knows whether the node at that path is a file or a directory.
* Bad, because it adds conversion and wrapping complexity before the base repository-path model is fully established.
* Bad, because it risks over-designing around boundary formats such as trailing slashes.

## More Information

This ADR describes the intended steady-state path architecture, not a temporary migration shape.

Current implementation status:

* `PathSegment` and `RelativePath` are implemented in `src/Arius.Core/Shared/Paths/`
* the earlier helper-only `RepositoryRelativePath` direction has been removed rather than retained as a long-lived adapter
* archive pipeline state and related shared filetree boundaries now use typed repository paths
* `RelativePath` currently owns explicit platform-boundary operations including `FromPlatformRelativePath(...)` and `ToPlatformPath(rootDirectory)`, but rooted local-path ownership should move toward `LocalRootPath` and `RootedPath`
* feature-local archive helpers such as `ToRelativePathText`, `GetLocalPath`, and `GetFileTreePath` were removed in favor of typing state and shared boundaries first

The next adoption slice is to introduce `LocalRootPath` and `RootedPath`, then move archive, restore, and list local-path boundaries and internals onto those types while preserving string boundaries only where they remain intentional.

Related design and plan documents:

* `docs/superpowers/specs/2026-05-04-relative-path-domain-model-design.md`
* `docs/superpowers/specs/2026-05-04-local-root-and-rooted-path-design.md`
* `docs/superpowers/plans/2026-05-04-relative-path-domain-slice-1.md`
* `docs/superpowers/plans/2026-05-04-relative-path-domain-slice-2-archive-boundary.md`
* `docs/superpowers/plans/2026-05-04-relative-path-domain-slice-3-list-restore.md`
