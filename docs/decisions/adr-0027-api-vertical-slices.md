---
status: accepted
date: 2026-11-18
decision-makers: Wouter Van Ranst
consulted: Claude (Anthropic)
informed: Arius agent team
confidence: high
---

# Organise Arius.Api as vertical slices with a thin dispatcher hub

## Context and Problem Statement

`Arius.Api` was laid out by technical layer: `Endpoints/`, `Hubs/`, `Jobs/`, `AppData/`, `Contracts/`, `Downloads/`, `Composition/`. Adding or changing one capability (say, restoring) meant touching a shared DTO file, an endpoints class, the hub, a forwarder file and the job runner, scattered across folders; `JobsHub` and `Contracts/Dtos.cs` grew into catch-all files; and nothing stopped a layer from reaching into another's internals. `Arius.Core` already solves this with `Features/` (vertical slices) over `Shared/` ([ADR-0010](adr-0010-use-feature-handlers-for-application-use-cases.md)), and the Api's tests and docs would be easier to navigate if they mirrored it.

Two Core-boundary bypasses also lived in the Api (the hub listed containers through the blob service factory; the stats endpoint read `ISnapshotService`), unseen because nothing enforced the host rule of [ADR-0013](adr-0013-core-host-separation.md) inside the Api.

The question for this ADR is how to structure `Arius.Api` so a capability lives in one place and the boundaries are checked mechanically.

## Decision Drivers

* One capability, one place: a reader (or agent) should find an endpoint, its DTOs and its behaviour together.
* Routes, hub method names and wire shapes are unchanged; the Angular SPA is untouched.
* Mirror `Arius.Core`'s `Features/` vs `Shared/` split so the whole solution reads the same way.
* Boundaries are enforced by architecture tests, not convention.
* `JobsHub` has one fixed class and one route, so the structure must work around SignalR's single-hub-class model.

## Considered Options

* A. Vertical slices: `Features/<Area>/` with one file per endpoint plus a DTO file; `Shared/` for what two areas need; a thin dispatcher hub.
* B. A folder per endpoint, as in Core.
* C. A file per resource (`AccountEndpoints.cs` holding every account route and DTO).
* D. A `partial` `JobsHub` class with one file per hub method.

## Decision Outcome

Chosen option: "A: vertical slices with a thin dispatcher hub", because it keeps each endpoint's behaviour and wire contract together at a size a reviewer can read in one screen, without the folder sprawl of B or the catch-all files of C and the original layout.

Confidence: high. It is a behaviour-preserving restructuring, covered by the existing Api unit and integration tests and by the new architecture rules.

Before:

```text
Arius.Api/{Endpoints,Hubs,Jobs,AppData,Contracts,Downloads,Composition}/
```

After:

```text
Arius.Api/Features/<Area>/<Endpoint>.cs + <Endpoint>Dtos.cs   (namespace Arius.Api.Features.<Area>)
Arius.Api/Shared/{AppData,Composition,Jobs,Downloads,Hubs,Hosting}/   (namespace Arius.Api.Shared.<Area>)
```

Rules:

* A feature area is a folder under `Features/` (Accounts, Repositories, Jobs, Archive, Restore, Downloads, …). An endpoint is one class named after it, in the request-endpoint-response style: it implements `IEndpoint`, takes its services through its constructor, maps its own full route in a static `Map(IEndpointRouteBuilder)` and handles it in an instance `Handle`/`HandleAsync`. The route delegate is a lambda that only binds the wire parameters and forwards to the resolved endpoint, so the OpenAPI description is inferred exactly as for an inline handler. Its request/response records sit in `<Endpoint>Dtos.cs`, and a DTO shared inside the area gets its own file. Pure helpers several endpoints of an area share stay static in `<Area>Endpoints.cs`. Background services and Core-event forwarders live in the area they serve.
* Hub methods are feature files too: an `IHubHandler` class with constructor-injected services and an instance `HandleAsync`. SignalR only dispatches to methods on the hub class, so `JobsHub` keeps a one-line method per hub method that receives the handler as a method-injected service and forwards to it. The handlers are `public` because they appear in the public hub's signatures.
* Endpoints and hub handlers are found by an assembly scan (`Shared/Hosting/FeatureRegistration`): adding the file registers and maps it. No framework (e.g. FastEndpoints) is used, which keeps minimal-API binding and the generated OpenAPI document unchanged.
* Promote to `Shared/` when a second area needs it. Until then a type stays in its area.
* Namespaces mirror folders, and the `Arius.Api.Tests` and `Arius.Api.Integration.Tests` projects mirror the same tree.

Enforced by `ApiArchitectureTests`: internal Api types stay within their namespace subtree unless marked `[SharedWithinAssembly]`; feature areas do not depend on each other; `Shared` does not depend on `Features`; the Api does not depend on Core `Shared` services (the Azure repository composer is the one seam); `AriusApiHost` and `JobsHub` are the composition roots allowed to reference every feature. Fixing the two bypasses was part of the work: `StreamContainers` now sends Core's `ContainerNamesQuery` through a repository-less provider, and the stats endpoint uses Core's `LatestSnapshotVersionQuery`. Design: [Web host](../design/hosts/web.md#api-layout).

### Consequences and Tradeoffs

* Good, because an endpoint's route, DTOs and behaviour are found together, and a new endpoint is one new file, with no registration line.
* Good, because the Api's boundaries (feature isolation, Core-through-`IMediator`) are now executable, like Core's.
* Good, because Api tests and docs mirror the source tree.
* Bad, because there are many small files (an endpoint file and usually a DTO file each).
* Bad, because `JobsHub` still has to list every hub method as a one-line forwarder, and because it references every feature it is a composition root exempt from the isolation rule.
* Bad, because a feature's dependencies are resolved on first use instead of at startup; `FeatureRegistrationTests` resolves every endpoint and hub handler from the production composition to catch a missing registration.
* Bad, because ArchUnitNET does not see what the body of an `async IAsyncEnumerable` method uses, so the hub's streaming features (`StreamContainers`, `StreamEntries`, `SearchAll`) are checked on their signatures and constructor dependencies only and rely on review.

### Confirmation

`Arius.Architecture.Tests/ApiArchitectureTests.cs` fails the build on a cross-area dependency, a `Shared` to `Features` dependency, a namespace-escaping internal type or an Api dependency on a Core `Shared` service. The existing `Arius.Api.Tests`, `Arius.Api.Integration.Tests` and the Playwright suites confirm routes, hub names and wire shapes did not move.

## Pros and Cons of the Options

### A. Vertical slices with a thin dispatcher hub

* Good, because it matches Core's mental model and keeps files small.
* Bad, because of the many small files and the one-line hub forwarders recorded above.

### B. A folder per endpoint, as in Core

* Good, because it would match Core's per-handler folders exactly.
* Bad, because an Api endpoint is a dozen lines plus its DTOs; a folder each would be mostly empty and bury the area.

### C. A file per resource

* Good, because it is the fewest files.
* Bad, because it recreates the catch-all file at area scale, mixes unrelated endpoints' DTOs and makes every endpoint change touch a shared file.

### D. A `partial` hub class, one file per method

* Good, because each hub method would live in its own file with no forwarder.
* Bad, because it spreads one class across the whole tree, tying `Shared/Hubs` to every feature in a way that is hard to see or check.

## More Information

- Host design: [Web host](../design/hosts/web.md#api-layout)
- Project conventions: [`src/Arius.Api/AGENTS.md`](https://github.com/woutervanranst/Arius7/blob/master/src/Arius.Api/AGENTS.md)
- Related: [ADR-0010](adr-0010-use-feature-handlers-for-application-use-cases.md) (Core's feature handlers), [ADR-0013](adr-0013-core-host-separation.md) (hosts drive Core only through `IMediator`), [ADR-0022](adr-0022-scripted-fake-core-test-harness.md) (the Api test harness)
