---
status: accepted
date: 2026-11-21
decision-makers: Wouter Van Ranst
consulted: Claude (Anthropic)
informed: Arius agent team
confidence: high
---

# Resolve Mediator handlers per message (Transient lifetime)

## Context and Problem Statement

The source-generated mediator defaults to a `Singleton` lifetime. In that mode its `ContainerMetadata` resolves every discovered handler, with its full dependency graph, on the mediator's first use. A provider that serves only some of Core's requests therefore fails unless it registers a stand-in for every other handler, and each stand-in list must match `AddArius`. Three such lists existed: `AddAriusWithoutRepository` (the Api's repository-less provider), `AddRootCorePlaceholders` (Explorer's root container), and the mocks in `CliHarness`.

The question for this ADR is how a provider can serve a subset of Core's requests without registering the rest.

## Decision Drivers

* A provider registers only the services of the requests it serves (e.g. `ContainerNamesQuery` needs only an `IBlobServiceFactory`).
* No handler lists that must match `AddArius`.
* The per-repository singleton service graph stays as it is ([service lifetimes](../design/cross-cutting/service-lifetimes.md)).

## Considered Options

* Keep `Singleton` and maintain stand-in lists
* `Transient` lifetime: resolve a handler when its message is sent
* `Scoped` lifetime
* One root container with a scoped repository context

## Decision Outcome

Chosen option: "`Transient` lifetime", because one assembly attribute per generator-running assembly (`Arius.Api`, `Arius.Cli`, `Arius.Explorer`) removes the need for stand-ins. Services keep their own registrations, so the repository graph and `AddArius`'s handler factories remain per-provider singletons.

Confidence: high. The Core, Cli, Api, Api integration and architecture suites pass. `StorageAccountInfoQueryDispatchTests` sends through the Transient mediator to an `AddArius` singleton handler.

Before:

```csharp
services.AddMediator();
services.AddAriusWithoutRepository();   // ContainerNamesQuery + a throwing stand-in per repository-bound handler
```

After:

```csharp
[assembly: MediatorOptions(ServiceLifetime = ServiceLifetime.Transient)]

services.AddMediator();
services.AddSingleton(blobServiceFactory);
```

### Consequences and Tradeoffs

* Good, because a provider registers only what its requests use, and no stand-in list has to match `AddArius`.
* Good, because a repository provider builds a handler's graph only when that handler is first used.
* Bad, because each send or publish resolves its handler from DI. Per-file notifications allocate a small handler instance per event, which is negligible next to hashing and blob IO.
* Bad, because a misconfigured handler fails when its message is sent, not on the mediator's first use.
* Neutral, because handlers must be stateless: an instance lives for one message. State belongs in injected singletons (`ProgressState`, `JobSink`).

### Confirmation

* `RepositoryLessProviderTests` (Api): a provider with only `AddMediator()` and the blob service factory lists an account's containers through the mediator.
* `StorageAccountInfoQueryDispatchTests` (Cli): the Transient mediator dispatches to an `AddArius` singleton handler.

## Pros and Cons of the Options

### Keep `Singleton` and maintain stand-in lists

* Good, because it allocates least per message and reports a missing registration on first use.
* Bad, because every provider that serves a subset needs a stand-in list that matches `AddArius`.

### `Scoped` lifetime

* Good, because it also resolves handlers lazily, and it is the usual choice when handlers depend on per-request services.
* Bad, because Arius providers create no scopes: `IMediator` and its handlers would be resolved from the root provider, which scope validation rejects.
* Bad, because `IMediator` becomes scoped while the singleton `ArchiveCommandHandler` and `RestoreCommandHandler` inject it, which is a captive dependency.
* Neutral, because `Transient` already supports a future with scopes: a transient handler resolved from a scope receives that scope's services, provided `IMediator` is resolved from the scope.

### One root container with a scoped repository context

* Good, because it is the conventional multi-tenant structure.
* Bad, because it restructures the per-repository provider model (single-shot chunk index, per-job event isolation) when a lifetime setting is enough.
