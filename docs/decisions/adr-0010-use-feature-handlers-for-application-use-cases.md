---
status: "accepted"
date: 2026-05-29
decision-makers: ["Wouter Van Ranst"]
consulted: ["OpenCode"]
informed: ["Arius maintainers"]
confidence: "high"
---

# Core use cases go through command and query handlers

## Context

Arius.Core exposes repository behavior to hosts such as the CLI and Explorer. The intended boundary is simple: hosts ask Core to run commands or queries; Core implementation types stay behind that boundary.

Mediator currently requires command/query contracts, notification contracts, and handlers to be public for generated dispatch and dependency-injection wiring. That compiler/tooling limitation must not be treated as API intent. Public accessibility in Core does not automatically mean a type is a supported host-facing entry point.

## Decision

All Core functionality is accessed through a command handler or query handler.

Hosts dispatch commands and queries. Handlers coordinate repository workflows. Shared services and other Core implementation types provide mechanics used by handlers and by other Core implementation types.

## Rules

* Hosts must not call Core shared services directly to perform repository behavior.
* Every host-facing Core operation must have a command or query contract and a handler under `src/Arius.Core/Features/`.
* Command and query handlers may call Core shared services directly.
* Core shared services may call other Core shared services when implementing reusable repository mechanics.
* Core implementation types should be `internal` unless Mediator, dependency injection, cross-assembly adapters, or contract use require public accessibility.
* A public Core type is still treated as internal implementation detail unless it is one of the allowed boundary types below.
* Allowed Core boundary types are command/query contracts, command/query result contracts, notification contracts, domain value types used by those contracts, Core-defined exceptions, storage adapter interfaces/models required by non-Core storage implementations, `ServiceCollectionExtensions`, `AssemblyMarker`, and explicitly documented composition/storage helpers such as `RepositoryLocalStatePaths`.
* Command and query handlers are public only because Mediator-generated infrastructure requires it; non-Core handwritten code must not depend on them directly.
* New exceptions to this boundary must be documented here before the architecture test is relaxed.

## Consequences

* Thin workflows still need a command or query instead of direct service access.
* Core service APIs remain reusable implementation mechanics, not accidental application endpoints.
* Architecture tests should enforce the boundary and keep only narrow mechanical exceptions. The ADR owns the policy; tests should not become the only place where exceptions are explained.
