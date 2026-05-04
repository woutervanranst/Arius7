---
status: proposed
date: 2026-05-04
decision-makers: Wouter Van Ranst, OpenCode
---

# Adopt Kind-Neutral Relative Path Domain Types

## Context and Problem Statement

Arius already treats archived paths as a canonical repository-internal form: forward-slash separated, relative to the archive root, and independent from local operating-system path syntax. However, most of that domain still moves through the codebase as raw `string` values. Validation, normalization, joining, root handling, and display rules are therefore scattered across archive, list, restore, filetree, fixtures, and E2E dataset code.

The question for this ADR is how Arius should represent repository-internal paths in the domain model so path invariants live in the type system instead of in repeated string helpers.

## Decision Drivers

* repository-internal paths are a core Arius domain concept, not incidental formatting
* archive, list, restore, and filetree workflows should operate on canonical relative paths as early as possible
* the same logical path should not need repeated normalization and validation throughout the codebase
* local operating-system paths and repository-internal paths have different semantics and should not be conflated
* repository path identity must remain stable across machines and must not inherit host filesystem comparison rules
* directory display conventions such as a trailing slash should not redefine the identity of the underlying path
* the model should remove illegal states while remaining practical to migrate incrementally

## Considered Options

* Keep string-based path helpers and continue centralizing validation in helper methods
* Introduce a kind-neutral path domain with `PathSegment` and `RelativePath`
* Introduce a typed path hierarchy immediately, including `FilePath`, `DirectoryPath`, and specialized entry-name types

## Decision Outcome

Chosen option: "Introduce a kind-neutral path domain with `PathSegment` and `RelativePath`", because it captures the real Arius domain path identity without forcing directory/file intent into the path itself and without preserving the current stringly helper sprawl.

`RelativePath` becomes the primary repository-internal path value object. It represents a canonical path inside the archived repository tree, not a host operating-system path. Its identity is kind-neutral: the path value does not itself mean file or directory. Root is represented explicitly as `RelativePath.Root`, not as a raw empty string at API boundaries.

`RelativePath` uses stable repository-path identity rules: it is case-preserving and uses ordinal, case-sensitive equality and hashing. Arius must not inherit comparison behavior from the current host operating system. Windows- or Linux-specific filesystem matching remains an explicit boundary concern where Arius crosses into the local filesystem.

`PathSegment` becomes the primitive value object for one canonical segment. A segment is non-empty, not whitespace-only, contains no separators, and rejects `.` and `..`.

Directory-vs-file meaning remains metadata on repository entries and filetree nodes, not part of the `RelativePath` identity. A trailing slash remains a serializer or display convention for directory entries, not part of the path value object.

`RelativePath` also does not own loose selector normalization or local filesystem path-joining and containment rules. Those remain boundary-specific behaviors layered around the core path value object.

This decision does not require Arius to introduce `FilePath` and `DirectoryPath` immediately. Those may be added later if concrete APIs show a strong need for kind-specific compile-time constraints, but the base path model is intentionally kind-neutral.

### Consequences

* Good, because archive, list, restore, filetree, and test infrastructure can share one canonical repository-path model instead of repeating validation and normalization logic.
* Good, because local disk paths and repository-internal paths become clearly separated concepts.
* Good, because repository path equality and hashing stay stable across machines instead of drifting with host filesystem behavior.
* Good, because root, parent/child composition, and segment rules can be expressed once on the type instead of reimplemented ad hoc.
* Good, because directory display rules such as `photos/` stay boundary-specific rather than leaking into the domain identity of `photos`.
* Bad, because adopting `RelativePath` broadly will require a large signature migration across models, handlers, tests, and serializers.
* Bad, because some existing string-based query and display surfaces will need intermediate adapters during the migration.

### Confirmation

The decision is being followed when repository-internal paths in archive, list, restore, filetree, and related tests are represented as `RelativePath` rather than raw `string`, and when single path components are represented as `PathSegment` where segment identity matters.

The decision is not being followed when new code introduces fresh canonical-path validators, normalizers, or join helpers over raw strings inside the repository domain.

Code review should check that:

* canonical repository paths are parsed at boundaries and then carried as `RelativePath`
* `RelativePath` equality and hashing remain ordinal and case-sensitive regardless of host OS
* local operating-system paths stay outside the domain model
* filetree and listing code do not use trailing slash as path identity
* selector normalization and local path-joining logic remain in boundary-specific APIs rather than on `RelativePath`
* new string helpers are not added where a path operation belongs on `RelativePath` or `PathSegment`

Tests should verify canonical parsing, root handling, path composition, parent/name semantics, prefix semantics where applicable, and formatting behavior at serializer or UI boundaries.

## Pros and Cons of the Options

### Keep string-based path helpers and continue centralizing validation in helper methods

This extends the current direction of extracting helper methods without changing the domain model.

* Good, because it has the lowest immediate migration cost.
* Good, because it can reduce the worst duplication quickly.
* Bad, because canonical paths still travel through the domain as raw strings.
* Bad, because helper sprawl tends to reappear whenever a new boundary or formatting rule is introduced.
* Bad, because root, segment, parent, and composition semantics remain conventions instead of types.

### Introduce a kind-neutral path domain with `PathSegment` and `RelativePath`

This is the chosen design.

* Good, because it models the true Arius repository-path identity directly.
* Good, because it separates path identity from file-vs-directory interpretation and from display syntax.
* Good, because it fixes equality and hashing semantics to stable ordinal behavior across machines.
* Good, because it gives one central home for validation, composition, and root semantics.
* Good, because it can be adopted incrementally while still aiming at a cleaner final model.
* Bad, because the initial migration touches many core types and tests.

### Introduce a typed path hierarchy immediately, including `FilePath`, `DirectoryPath`, and specialized entry-name types

This would extend the type system further from the start.

* Good, because some APIs could express stronger intent at compile time.
* Good, because directory-specific or file-specific operations could be made explicit.
* Bad, because Arius often knows a path before it knows whether the node at that path is a file or a directory.
* Bad, because it adds conversion and wrapping complexity before the base path domain is established.
* Bad, because it risks over-designing around boundary formats such as trailing slashes.

## More Information

This ADR establishes the architectural direction for a future typed-path migration. The initial helper-based `RepositoryRelativePath` validator is treated as a transitional step, not the target end state.

Planned core types:

* `PathSegment`
* `RelativePath`

Likely future operations on `RelativePath`:

* `Root`
* `IsRoot`
* `Parent`
* `Name`
* ordinal, case-sensitive equality and hashing
* path composition, potentially including `/` operator support
* canonical parsing and formatting

Likely migration boundary:

* local filesystem paths remain string or dedicated local-path concepts at infrastructure boundaries
* repository-internal paths become `RelativePath` in domain and feature models
* filetree directory trailing-slash formatting remains serializer-specific
