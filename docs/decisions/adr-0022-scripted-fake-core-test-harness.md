---
status: accepted
date: 2026-07-05
decision-makers: Wouter Van Ranst
consulted: Claude (Anthropic)
informed: Arius maintainers
confidence: high
---

# Scripted-fake-Core harness for Api/Web test coverage

## Context and Problem Statement

`Arius.Api` had zero HTTP/hub-level test coverage. The only integration path exercising it was a Playwright e2e suite that boots the **real** Api against **real Azure Blob Storage** and runs a **real archive** — slow, non-hermetic, and unable to fabricate the tier/cost/rehydration/warning scenarios that most jobs-progress defects live in (an xhigh code review of the jobs-progress branch found 15 such defects, almost none of which had a regression test). The owner's constraint was explicit: no `Arius.Core` behavior changes — the fix has to be Api/Web-side.

The question for this ADR is how Api/Web get deterministic, offline coverage of archive/restore progress, tier, cost-approval, rehydration, and warning scenarios without changing `Arius.Core` or giving up the real Mediator pipeline, event forwarders, `JobSink`, and SignalR hub as the thing under test.

## Decision Drivers

* No `Arius.Core` changes — every seam must live in `Arius.Api`.
* Scenarios (tier, cost, rehydration timelines, warning counts) must be scriptable deterministically and offline — real Azure cannot fabricate "awaiting-cost, 282 archive-tier chunks, ready in 13h".
* The real Mediator pipeline, forwarders, `JobSink`, and SignalR hub should be exercised as-is, not mocked away — a fake that replaces too much stops testing the Api.
* Production `Arius.Api` must ship with zero test-framework references and zero environment branches.
* The same fake must be reachable both in-process (fast TUnit integration tests) and out-of-process (a real browser driving a real HTTP/SignalR server, for Playwright).

## Considered Options

* **Deep-fake** — fake `Arius.Core` at a low level (e.g. a fake `IBlobContainerService`) and let the real command handlers run against it.
* **Hybrid** — real Core for some scenarios, a fake for others, selected per test.
* **Scripted fake Core** — replace the Core command handlers themselves with fakes that replay a scripted sequence of the real `INotification` events, selected per repository from a registry.
* Status quo — real-Azure Playwright only, add unit tests where possible.

## Decision Outcome

Chosen option: "scripted fake Core", because it is the only option that makes every progress/tier/cost/rehydration/warning combination directly scriptable while leaving the real Mediator pipeline, forwarders, `JobSink`, and SignalR hub untouched. A deep-fake still runs the real handlers' control flow, so it cannot fabricate scenarios the real logic wouldn't reach on `FakeInMemoryBlobContainerService` (e.g. "1000 pointer-only deduped files + 10 new 100 MB uploads" or "park at awaiting-cost with a specific rehydration window") without contorting the fake storage layer to force them.

Confidence: high. The harness has been in continuous use since 2026-07-05 across three plans (harness, representation, lifecycle/reattach) and the hermetic Playwright suite; `dotnet test src/Arius.Api.Integration.Tests` and `npm run e2e:hermetic` are green CI gates.

Before:

```csharp
// RepositoryProviderRegistry.BuildAsync — inline, untestable without real Azure
var blobService   = await blobServiceFactory.CreateAsync(connection.AccountName, connection.AccountKey, ct);
var blobContainer = await blobService.OpenContainerServiceAsync(connection.Container, mode, ct);
services.AddAzureBlobStorage();
services.AddArius(blobContainer, connection.Passphrase, connection.AccountName, connection.Container);
```

After:

```csharp
// RepositoryProviderRegistry.BuildAsync — Core composition behind a swappable seam
services.AddMediator();
await _coreComposer.ComposeAsync(services, connection, mode, cancellationToken);
// production: AzureRepositoryCoreComposer → AddAzureBlobStorage() + AddArius(...), byte-identical to before
// tests:      ScriptedRepositoryCoreComposer → per-repo ScenarioRegistry scenario + NotConfigured stand-ins
```

### Consequences and Tradeoffs

* Good, because every jobs-progress finding (cost handshake, reattach state, single-active-job guard, warning counts, byte/counter representation) got a concrete, deterministic regression test that would have been impractical against real Azure.
* Good, because the same scripted fake serves three tiers unchanged: `Arius.Api.Integration.Tests` (in-process `WebApplicationFactory`), `Arius.Api.FakeTestHost` (out-of-process executable for hermetic Playwright), and — untouched — production `Arius.Api`.
* Good, because production isolation is structural, not conventional: `Arius.Api` never references `Arius.Api.FakeTestHost`, and the scripted composer wins only because that separate host pre-registers it before `AddAriusApi()`'s `TryAddSingleton<IRepositoryCoreComposer, AzureRepositoryCoreComposer>()` runs — no `IsEnvironment` branch anywhere in production code.
* Bad, because the fake's only fidelity guard today is compile-time coupling (it emits the real Core event/result/DTO types, so a renamed field breaks the build); there is no runtime check that a canonical scripted scenario's event *sequence* still matches what real Core actually emits. A drift alarm (real Core vs `FakeInMemoryBlobContainerService`, diffed against the canonical scenarios) was scoped but not built — see the [testing design doc](../history/superpowers/2026-07-05-jobs-progress-test-harness-and-fixes-design.md) §2.4.
* Bad, because Mediator eagerly resolves every discovered command/query/stream-query handler on first `Send`/`Publish`, not just the one invoked — the scripted composer must register a `NotConfigured*Handler` stand-in for every Core command/query it doesn't script, or an unrelated call throws a DI resolution error instead of the intended `NotSupportedException`.
* Neutral, because the existing real-Azure Playwright suite (`e2e/specs/**`) is kept as-is rather than converted — it remains the full-stack behavioral gate; the hermetic suite is additive.

### Confirmation

* `dotnet test src/Arius.Api.Integration.Tests/Arius.Api.Integration.Tests.csproj` — all green (job lifecycle, reattach, cost handshake, single-active-job guard, stale-approval sweep, concurrent-resume smoke).
* `cd src/Arius.Web && npm run e2e:hermetic` — all green (jobs-live-update #7, cost-reattach/cost-online-restore #2, rehydrating-reattach #13/#14, single-active-job #1).
* `grep -rn "AddArius\|AddAzureBlobStorage" src/Arius.Api/Composition/RepositoryProviderRegistry.cs` returns nothing — the registry only calls `AddMediator()` + `_coreComposer.ComposeAsync(...)`, confirming production composition still lives entirely inside `AzureRepositoryCoreComposer`.

## Pros and Cons of the Options

### Deep-fake (fake `IBlobContainerService`, real handlers)

* Good, because it exercises the real command-handler control flow, including edge cases inside Core's own logic.
* Bad, because scenarios are bounded by what the real handlers reach given a fake storage backend — some jobs-progress findings (specific cost/rehydration timelines, exact warning counts) are not reachable this way without elaborate storage-layer scripting.
* Bad, because `Arius.Core.Tests`/`Arius.Integration.Tests` already own this tier (`FakeInMemoryBlobContainerService`, Azurite) — duplicating it at the Api layer would blur ownership.

### Hybrid (real Core for some scenarios, fake for others)

* Neutral, because it can reach both deep and scripted scenarios.
* Bad, because it means maintaining two composition paths with different fidelity/flexibility tradeoffs, doubling the harness surface for marginal gain over a single scripted-fake path plus the existing Core-level fixture hierarchy.

### Scripted fake Core

* Good, because any scenario expressible as an ordered list of real Core `INotification`s (plus an optional cost prompt) is directly scriptable, independent of what real Core's control flow would produce.
* Good, because the seam (`IRepositoryCoreComposer`) is a pure extraction — production behavior is byte-identical, so the change carries no behavioral risk to ship.
* Bad, because scripted scenarios can drift from what real Core would actually emit if nothing enforces it (see Consequences).

### Status quo (real-Azure Playwright only)

* Bad, because it is slow, requires live Azure credentials, and cannot deterministically fabricate the tier/cost/rehydration/warning scenarios the findings live in — the reason this ADR exists.

## More Information

- Design doc (original intent, before implementation): [2026-07-05 test-harness design](../history/superpowers/2026-07-05-jobs-progress-test-harness-and-fixes-design.md), plus its three implementation plans — [harness](../history/superpowers/2026-07-05-jobs-progress-plan-1-harness.md), [client + Vitest](../history/superpowers/2026-07-05-jobs-progress-plan-3b-client.md), [browser-hermetic e2e](../history/superpowers/2026-07-06-jobs-progress-plan-3c-browser-e2e.md).
- Implementation: `src/Arius.Api/Composition/{IRepositoryCoreComposer,AzureRepositoryCoreComposer}.cs`, `src/Arius.Api/AriusApiHost.cs`, `src/Arius.Api.FakeTestHost/`, `src/Arius.Api.Integration.Tests/Harness/AriusApiFactory.cs`.
- Test tier details: [cross-cutting/testing.md](../design/cross-cutting/testing.md).
