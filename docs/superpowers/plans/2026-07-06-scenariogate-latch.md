# ScenarioGate Release-Before-Wait Latch — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the test harness's `ScenarioGate` a proper latch so a `Release` that arrives *before* the scripted handler reaches `WaitForRelease` is remembered and honoured — eliminating the latent flakiness where a promptly-released gated scenario would otherwise hang until the Playwright timeout (review #12) — and remove the duplicated `WaitUntil` polling helper across the integration test classes (review #17).

**Architecture:** `ScenarioGate` currently drops a release with no waiter (`Release` no-ops when the key is absent), so a later `WaitForRelease` creates a fresh, never-signalled `TaskCompletionSource` and awaits forever. The fix makes `Release` **create-or-complete** the per-repo `TaskCompletionSource` (a sticky latch), so release-before-wait and wait-before-release both resolve immediately/promptly. A shared `ScenarioWait.Until` helper replaces the three copy-pasted `WaitUntil` methods in the integration tests.

**Tech Stack:** .NET 10, C#; `System.Threading.Tasks.TaskCompletionSource` + `ConcurrentDictionary`; TUnit (`Microsoft.Testing.Platform`) for tests. The gate is consumed by `ScriptedArchiveHandler`/`ScriptedRestoreHandler` (`src/Arius.Api.Testing`) and released over HTTP by `POST /api/testing/release/{repoId}` (`TestingControlEndpoints`) in the hermetic Playwright suite.

## Global Constraints

- Target framework: **net10.0**; keep `ScenarioGate` `sealed`, file-scoped namespace, matching the existing style in `src/Arius.Api.Testing/ScenarioGate.cs`.
- `ScenarioGate` lives in the **`Arius.Api.Testing`** assembly (test/e2e host only) — it is never wired into the production host, so this change has zero production surface.
- Preserve the existing public surface used by callers: `Task WaitForRelease(long repositoryId, CancellationToken ct)`, `void Release(long repositoryId)`, `void ReleaseAll()`. Do not change signatures — `ScriptedArchiveHandler`, `ScriptedRestoreHandler`, and `TestingControlEndpoints` call them as-is.
- The latch is **sticky until reset**: once a repo is released it stays released until `ReleaseAll()` (called by `POST /api/testing/reset` between hermetic specs). This matches the test lifecycle (one gated run per repo per scenario).
- Run tests with `dotnet test --project src/Arius.Api.Integration.Tests`.
- Keep every commit green: `dotnet build src/Arius.slnx` must compile after each task.

---

## File Structure

**Modified:**
- `src/Arius.Api.Testing/ScenarioGate.cs` — `Release` becomes create-or-complete (the latch fix).

**Created:**
- `src/Arius.Api.Integration.Tests/ScenarioGateTests.cs` — unit tests for both release orderings.
- `src/Arius.Api.Integration.Tests/Harness/ScenarioWait.cs` — shared polling helper.

**Modified (dedupe #17):**
- `src/Arius.Api.Integration.Tests/SingleActiveJobScenarioTests.cs`
- `src/Arius.Api.Integration.Tests/RestoreCostHandshakeTests.cs`
- `src/Arius.Api.Integration.Tests/ReattachScenarioTests.cs`

---

## Task 1: Make `ScenarioGate.Release` a create-or-complete latch (fixes #12)

**Files:**
- Modify: `src/Arius.Api.Testing/ScenarioGate.cs`
- Test: `src/Arius.Api.Integration.Tests/ScenarioGateTests.cs`

**Interfaces:**
- Behaviour change (no signature change): after `Release(repoId)`, a subsequent `WaitForRelease(repoId, ct)` completes without blocking; `Release` before any `WaitForRelease` is remembered. `WaitForRelease` before `Release` still completes when `Release` fires. `ReleaseAll()` completes all outstanding waiters and clears the latch state (so the next scenario starts fresh).

- [ ] **Step 1: Write the failing test**

Create `src/Arius.Api.Integration.Tests/ScenarioGateTests.cs`:

```csharp
using Arius.Api.Testing;

namespace Arius.Api.Integration.Tests;

public class ScenarioGateTests
{
    [Test]
    public async Task Release_before_wait_is_remembered()
    {
        var gate = new ScenarioGate();

        gate.Release(repositoryId: 1);                       // release arrives first (no waiter yet)
        var wait = gate.WaitForRelease(1, CancellationToken.None);

        await wait.WaitAsync(TimeSpan.FromSeconds(1));        // must complete promptly, not hang
        await Assert.That(wait.IsCompletedSuccessfully).IsTrue();
    }

    [Test]
    public async Task Wait_before_release_still_completes()
    {
        var gate = new ScenarioGate();

        var wait = gate.WaitForRelease(2, CancellationToken.None);
        await Assert.That(wait.IsCompleted).IsFalse();        // still gated

        gate.Release(2);
        await wait.WaitAsync(TimeSpan.FromSeconds(1));
        await Assert.That(wait.IsCompletedSuccessfully).IsTrue();
    }

    [Test]
    public async Task ReleaseAll_completes_outstanding_waiters()
    {
        var gate = new ScenarioGate();
        var a = gate.WaitForRelease(3, CancellationToken.None);
        var b = gate.WaitForRelease(4, CancellationToken.None);

        gate.ReleaseAll();

        await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(1));
        await Assert.That(a.IsCompletedSuccessfully && b.IsCompletedSuccessfully).IsTrue();
    }

    [Test]
    public async Task WaitForRelease_honours_cancellation_when_never_released()
    {
        var gate = new ScenarioGate();
        using var cts = new CancellationTokenSource();
        var wait = gate.WaitForRelease(5, cts.Token);

        cts.Cancel();
        await Assert.That(async () => await wait).Throws<OperationCanceledException>();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --project src/Arius.Api.Integration.Tests --filter "FullyQualifiedName~ScenarioGateTests"`
Expected: FAIL — `Release_before_wait_is_remembered` times out (the current `Release` no-ops when no waiter exists, so the later `WaitForRelease` awaits a fresh, never-signalled TCS).

- [ ] **Step 3: Make `Release` create-or-complete**

In `src/Arius.Api.Testing/ScenarioGate.cs`, change `Release` from:

```csharp
    public void Release(long repositoryId)
    {
        if (_gates.TryGetValue(repositoryId, out var tcs)) tcs.TrySetResult();
    }
```

to:

```csharp
    public void Release(long repositoryId)
    {
        // Create-or-complete: a release that arrives before the scripted handler reaches WaitForRelease must be
        // remembered, so the later WaitForRelease sees an already-completed latch instead of creating a fresh,
        // never-signalled TCS and hanging until the Playwright timeout (review #12). Sticky until ReleaseAll().
        _gates.GetOrAdd(repositoryId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }
```

Update the class-level XML doc comment to state the latch is sticky-until-reset (so future readers know `Release` before `WaitForRelease` is intentional):

```csharp
/// <summary>Per-repository release latch so a scripted run can be held mid-flight (e.g. an archive kept
/// "running" so a browser test can observe it in the Active list) until a control endpoint releases it.
/// A repo with no gated scenario never awaits it. <see cref="Release"/> is a sticky latch: a release that
/// arrives before the run reaches <see cref="WaitForRelease"/> is remembered (create-or-complete), so both
/// orderings resolve. Latch state is cleared by <see cref="ReleaseAll"/> (test reset).</summary>
```

Leave `WaitForRelease` and `ReleaseAll` unchanged — `WaitForRelease`'s `GetOrAdd` now returns the already-completed TCS in the release-first case, and `ReleaseAll` still completes-then-clears.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet build src/Arius.slnx`
Expected: builds clean.

Run: `dotnet test --project src/Arius.Api.Integration.Tests --filter "FullyQualifiedName~ScenarioGateTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Api.Testing/ScenarioGate.cs src/Arius.Api.Integration.Tests/ScenarioGateTests.cs
git commit -m "fix(testing): make ScenarioGate.Release a sticky latch so release-before-wait can't hang (review #12)"
```

---

## Task 2: Extract the shared `ScenarioWait.Until` helper (fixes #17)

**Files:**
- Create: `src/Arius.Api.Integration.Tests/Harness/ScenarioWait.cs`
- Modify: `src/Arius.Api.Integration.Tests/SingleActiveJobScenarioTests.cs`
- Modify: `src/Arius.Api.Integration.Tests/RestoreCostHandshakeTests.cs`
- Modify: `src/Arius.Api.Integration.Tests/ReattachScenarioTests.cs`

**Interfaces:**
- Produces: `static Task ScenarioWait.Until(Func<bool> condition, TimeSpan timeout)` — polls `condition` every 50 ms until true, throwing `TimeoutException` at the deadline. Replaces the three identical `private static async Task WaitUntil(...)` copies.

- [ ] **Step 1: Add the shared helper**

Create `src/Arius.Api.Integration.Tests/Harness/ScenarioWait.cs`:

```csharp
namespace Arius.Api.Integration.Tests.Harness;

/// <summary>Polls a condition until it holds or the timeout elapses — the shared spin-wait used across the
/// scenario integration tests (was a per-class copy; review #17). 50 ms cadence, throws on timeout.</summary>
public static class ScenarioWait
{
    public static async Task Until(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("Condition not met within timeout.");
    }
}
```

- [ ] **Step 2: Replace the three copies**

In each of `SingleActiveJobScenarioTests.cs`, `RestoreCostHandshakeTests.cs`, and `ReattachScenarioTests.cs`:

1. Ensure `using Arius.Api.Integration.Tests.Harness;` is present (it already is in each — they use `AriusApiFactory`).
2. Delete the file's `private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout) { … }` method.
3. Replace each call `await WaitUntil(<cond>, <timeout>);` with `await ScenarioWait.Until(<cond>, <timeout>);`.

For example, in `RestoreCostHandshakeTests.cs`:

```csharp
        await ScenarioWait.Until(() => db.GetJob(jobId)?.Status == "awaiting-cost", TimeSpan.FromSeconds(10));
```

- [ ] **Step 3: Build and run the affected tests**

Run: `dotnet build src/Arius.slnx`
Expected: builds clean; `grep -rn "private static async Task WaitUntil" src/Arius.Api.Integration.Tests` returns nothing.

Run: `dotnet test --project src/Arius.Api.Integration.Tests --filter "FullyQualifiedName~SingleActiveJobScenarioTests|FullyQualifiedName~RestoreCostHandshakeTests|FullyQualifiedName~ReattachScenarioTests"`
Expected: PASS (unchanged behaviour, now sharing one helper).

- [ ] **Step 4: Commit**

```bash
git add src/Arius.Api.Integration.Tests/Harness/ScenarioWait.cs src/Arius.Api.Integration.Tests/SingleActiveJobScenarioTests.cs src/Arius.Api.Integration.Tests/RestoreCostHandshakeTests.cs src/Arius.Api.Integration.Tests/ReattachScenarioTests.cs
git commit -m "test(api): extract shared ScenarioWait.Until helper (review #17)"
```

---

## Final verification

- [ ] **Full API suite:** `dotnet test --project src/Arius.Api.Integration.Tests` → all green.
- [ ] **Hermetic e2e (the real consumer of the gate):** `cd src/Arius.Web && npm run e2e:hermetic` → all green, including any gated scenario (`representativeArchive` / `rehydratingRestore` with `gated: true`).
- [ ] **Robustness check (optional):** add a temporary gated hermetic spec that calls `POST /api/testing/release/{repoId}` *immediately* after starting the job (before the run reaches the handler) and confirm it no longer hangs — then remove it. This is the exact race Task 1 closes.

---

## Self-Review notes

- **Spec coverage:** #12 → Task 1 (the latch); #17 → Task 2 (shared `WaitUntil`). Both are the ScenarioGate/test-harness findings from the review.
- **Type consistency:** `Release(long)` / `WaitForRelease(long, CancellationToken)` / `ReleaseAll()` signatures unchanged (callers untouched); new helper is `ScenarioWait.Until(Func<bool>, TimeSpan)`.
- **Why sticky-until-reset is safe:** each hermetic spec calls `POST /api/testing/reset` → `ReleaseAll()` which clears `_gates`, so a remembered release never leaks across scenarios. Within a scenario a repo has exactly one gated run, so a stale completed latch cannot mis-release a second run.
- **No production impact:** `ScenarioGate` is only registered by the `Arius.Api.Testing` host and `AriusApiFactory`; the production `AriusApiHost` never references it.
