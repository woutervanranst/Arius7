---
name: csharp-tunit
description: Use when writing or reviewing C# tests in a repository that uses TUnit, especially when choosing TUnit data sources, lifecycle hooks, parallelism controls, or async assertions.
---

# TUnit For C#

## Overview

TUnit's highest-value differences are discovery-time data generation, property injection, parallel-by-default execution, and async assertions. Prefer the smallest TUnit feature that solves the real testing problem; do not cargo-cult every attribute.

## High-Value Patterns

- Use `[Arguments(...)]` only for compile-time constants.
- Use `[MethodDataSource]` for computed or reusable data. For mutable reference data, return `Func<T>` so each test gets a fresh instance instead of sharing state.
- Use `TestDataRow<T>` when individual cases need `DisplayName`, `Skip`, or `Categories`.
- Use `[CombinedDataSources]` only when you really want the full Cartesian product.
- Use `[ClassDataSource<T>]` plus `SharedType` for expensive fixtures. Choose `PerClass`, `PerAssembly`, `PerTestSession`, or `Keyed` deliberately; broader sharing increases coupling and lifetime risk.
- Prefer property injection with `required` properties for non-trivial fixtures. TUnit can initialize and dispose nested dependency graphs for you.
- If you use Native AOT mode, keep method data sources static.

## Discovery Vs Execution

- `IAsyncDiscoveryInitializer` runs during test discovery, before test cases are generated.
- `IAsyncInitializer` runs during test execution, after the test instance is created and injected.
- `InstanceMethodDataSource` is evaluated during discovery. If it depends on execution-time initialization, you can silently get zero tests.
- Use `TestBuilderContext.Current` during discovery and `TestContext.Current` during execution.
- If a workflow test truly needs `DependsOn`, pass state through `TestContext.Current.StateBag` instead of static globals.

## Hooks And Fixtures

- Use `[Before(Test)]` and `[After(Test)]` for per-test setup and cleanup that belongs to the test class instance.
- Use `[Before(Class)]` and `[After(Class)]` for class-scoped coordination, remembering these hooks are static.
- Prefer fixture objects with `IAsyncInitializer` and `IAsyncDisposable` when setup is async, expensive, or shared across tests.
- Keep hooks thin. If setup grows into infrastructure orchestration, move it into a fixture and inject it.
- Use session- or assembly-level hooks sparingly; they are powerful but easy to overuse.

## Parallelism

- TUnit makes every test eligible to run concurrently by default.
- Use `[NotInParallel("key")]` for genuinely shared mutable resources.
- Use `[ParallelGroup("key")]` when one group may run internally in parallel but must not overlap with another group.
- Use `[ParallelLimiter<T>]` when tests may overlap but external capacity is bounded.
- Prefer isolated tests over both `[DependsOn]` and `[Order]`. If sequencing is unavoidable, prefer `[DependsOn]` for real result or state handoff and use `[Order]` only for simple sequencing inside a shared non-parallel scope.
- Treat `[Retry]` as a last resort after fixing the underlying race or eventual-consistency issue.

## Async And Diagnostics

- TUnit assertions are async; await them.
- Prefer `WaitsFor` or `Eventually` and `CompletesWithin` over `Task.Delay` sleeps.
- Use `TestContext.Current.Output` and artifacts for logs, screenshots, dumps, and failure diagnostics.
- When `WaitsFor` or `Eventually` fails, log the last observed state, resource identifier, and timing details so the failure is diagnosable.
- Use `TestContext.Current.Isolation` to generate unique resource names for parallel-safe tests.
- Do not assume another framework's CLI filtering flags map directly to a TUnit repo. Check the repo's configured TUnit/MTP invocation before using filters or list commands.

## Quick Reference

| Need | Prefer |
|---|---|
| dynamic non-constant data | `MethodDataSource` |
| per-case display name / skip / categories | `TestDataRow<T>` |
| expensive reusable fixture | `ClassDataSource<T>(Shared = ...)` |
| discovery-time async data loading | `IAsyncDiscoveryInitializer` |
| execution-time async setup | `IAsyncInitializer` |
| wait for eventual condition | `WaitsFor` / `Eventually` |
| block overlap on a shared resource | `NotInParallel("key")` |
| cap concurrency without serializing everything | `ParallelLimiter<T>` |

## Example

```csharp
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using TUnit.Core;
using TUnit.Assertions;

public record UserCase(string Email, bool IsValid);

public static class UserCases
{
    public static IEnumerable<TestDataRow<Func<UserCase>>> All()
    {
        yield return new(() => new UserCase("a@example.com", true), DisplayName: "valid email");
        yield return new(() => new UserCase("bad", false), DisplayName: "invalid email");
    }
}

public sealed class ApiFixture : IAsyncInitializer, IAsyncDisposable
{
    public HttpClient Client { get; private set; } = default!;

    public Task InitializeAsync()
    {
        Client = new HttpClient { BaseAddress = new Uri("https://example.test") };
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Client.Dispose();
        return ValueTask.CompletedTask;
    }
}

public class UserTests
{
    [ClassDataSource<ApiFixture>(Shared = SharedType.PerTestSession)]
    public required ApiFixture Api { get; init; }

    [Test]
    [MethodDataSource(typeof(UserCases), nameof(UserCases.All))]
    public async Task Validate_user_input(UserCase userCase)
    {
        var response = await Api.Client.PostAsJsonAsync("/users/validate", userCase);
        await Assert.That(response.IsSuccessStatusCode).IsEqualTo(userCase.IsValid);
    }
}
```

## Common Mistakes

- Using `[Arguments]` for objects or other non-constant values.
- Returning the same mutable reference from a data source and accidentally coupling tests.
- Using `InstanceMethodDataSource` with execution-time initialized state.
- Serializing the whole suite instead of scoping the parallel constraint to the actual shared resource.
- Using `[Order]` or `[Retry]` to hide poor isolation.
- Treating `TestContext` as available during discovery; use `TestBuilderContext` there instead.
- Copying outdated or incorrect names such as `MethodData`, `ClassData`, or `ParallelLimit`.
