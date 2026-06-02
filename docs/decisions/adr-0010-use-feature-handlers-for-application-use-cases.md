---
status: "accepted"
date: 2026-05-29
decision-makers: ["Wouter Van Ranst"]
consulted: ["OpenCode"]
informed: ["Arius maintainers"]
confidence: "high"
---

# Application use cases go through feature handlers

## Context and Problem Statement

Arius.Core has two kinds of code boundaries: feature handlers that orchestrate user-facing workflows, and shared services that implement reusable repository mechanics. The boundary can blur when a host-level action looks like a thin wrapper over one service method. Direct service calls from a host are tempting, but they bypass the same orchestration, logging, testing, and dependency-injection shape used by other Core use cases.

The question for this ADR is when hosts should call a Core service directly and when they should use a command or query handler.

## Decision Drivers

* User-facing workflows should be consistently testable without invoking the CLI.
* Hosts should stay thin and avoid owning repository workflow orchestration.
* Shared services should remain reusable mechanics, not accidental application endpoints.
* The Features versus Shared boundary should stay visible in code review.
* Simple one-step use cases should not create unnecessary abstractions below the feature layer.

## Considered Options

* Route every call into Core through a command or query handler.
* Use command/query handlers for application use cases and call services directly inside those handlers.
* Allow hosts to call shared services directly whenever the workflow is simple.

## Decision Outcome

Chosen option: "Use command/query handlers for application use cases and call services directly inside those handlers", because it keeps host-facing workflows consistent while preserving shared services as reusable implementation mechanics.

Confidence: high. This matches the existing archive, restore, and list shape, and gives future host-to-Core calls a stable boundary rule.

The practical effect of this decision should be visible at a glance:

Before:

```csharp
// Host owns the application use case.
var service = services.GetRequiredService<SomeSharedService>();
var result = await service.DoWorkflowAsync(cancellationToken);
```

After:

```csharp
// Host dispatches the use case; the handler owns orchestration.
var mediator = services.GetRequiredService<IMediator>();
var result = await mediator.Send(new SomeApplicationCommand(), cancellationToken);

// Feature handler uses shared mechanics.
var output = await sharedService.DoMechanicAsync(cancellationToken);
```

### Consequences and Tradeoffs

* Good, because each host-level operation maps to a Core feature command/query instead of embedding workflow decisions in the host.
* Good, because feature behavior can be tested at the handler level without CLI parsing and rendering concerns.
* Good, because shared services remain focused on repository mechanics such as chunk-index lookup, repair, flushing, filetree caching, snapshot persistence, and chunk storage.
* Good, because the rule is not absolute: low-level service methods do not need their own command handlers.
* Bad, because very small application use cases still need a command, result type, handler, and DI registration.
* Bad, because the Mediator source generator can make test service providers more sensitive to missing handler registrations.

### Confirmation

This decision is being followed when:

* A user-facing CLI verb, Explorer action, scheduled operation, or other host-level operation dispatches a Core command/query instead of calling a shared service directly.
* The corresponding handler lives under `src/Arius.Core/Features/` and coordinates shared services.
* Shared services under `src/Arius.Core/Shared/` are called from handlers or other shared services for reusable mechanics, not exposed directly as host workflows.

This decision is not being followed when a host directly calls a shared service to perform a user-facing workflow that should have a named command or query.

## Pros and Cons of the Options

### Route every call into Core through a command or query handler

This would require command/query wrappers for all Core interactions, including low-level mechanics.

* Good, because there is only one rule.
* Bad, because it creates boilerplate for operations that are not application use cases.
* Bad, because it obscures reusable service APIs behind unnecessary mediator indirection.

### Use command/query handlers for application use cases and call services directly inside those handlers

This is the chosen design.

* Good, because user-facing workflows have one consistent entry pattern.
* Good, because shared services stay composable and directly testable.
* Good, because handlers decide when to resolve snapshots, repair indexes, flush shards, or publish snapshots, while services decide how those mechanics work.
* Bad, because developers must still judge whether a call is an application use case or a reusable mechanic.

### Allow hosts to call shared services directly whenever the workflow is simple

This would permit hosts to bypass feature handlers for one-step workflows.

* Good, because the smallest simple workflows need less code.
* Bad, because simple workflows tend to accumulate logging, error handling, progress, and testing needs over time.
* Bad, because it spreads application orchestration across hosts and Core services.
* Bad, because it weakens the Features versus Shared architecture boundary.

## More Information

This decision applies to calls into Arius.Core from any host, including CLI, Explorer, tests that exercise host wiring, scheduled jobs, and future service hosts. Specific implementations may use different command or query names, but the boundary rule is the same: hosts dispatch application use cases; feature handlers coordinate shared services.
