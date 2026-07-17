# Jobs progress — Plan 3c: browser-hermetic e2e (scripted-Core Api host)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let Playwright drive real browser tests against Arius.Api booted **out-of-process with a scripted (fake) Arius.Core** — no Azure — and lock in the browser behaviour of #1 (single-active-job rejection), #2 (cost modal on reattach), #7 (jobs list live-update), and #13/#14 (auto-resume toggle + "≈ hydrated by" on a rehydrating reattach).

**Architecture:** The scripted-Core harness that today only works **in-process** (via `WebApplicationFactory.ConfigureServices`) is extracted into a new **executable** project `Arius.Api.Testing`, which reuses Arius.Api's pipeline through two new extension methods (`AddAriusApi()`/`MapAriusApi()`) but injects `ScriptedRepositoryCoreComposer` + a `Testing` control-endpoint group. Production `Arius.Api` keeps **zero** test references and **zero** environment branches — the composer seam is registered with `TryAdd`, so the test host's earlier registration wins. This mirrors the `CliHarness` pattern (inject the composition strategy, don't branch on env). A second Playwright config (`playwright.hermetic.config.ts`) boots that host, seeds repos + selects named scenarios over the control endpoint, and runs a new hermetic spec suite. The `Arius.Api.Integration.Tests` harness collapses onto the same extracted library (deletes its duplicate copy).

**Tech Stack:** ASP.NET Core minimal APIs + SignalR (net10.0), Othmar Mediator, TUnit (in-process integration), Playwright + Chromium (hermetic browser), Angular 21.

## Global Constraints

- **No `Arius.Core` changes.**
- **Production `Arius.Api` gains no reference to any test/scripting assembly and no `IsEnvironment` branch.** The scripted composer is selected only by the separate `Arius.Api.Testing` host pre-registering it before `AddAriusApi()`'s `TryAddSingleton`.
- **Do not reintroduce Karma/Jasmine.** Vitest (Plan 3b) is the unit runner; Playwright is the browser runner.
- **Do not touch the existing real-Azure Playwright specs** (`e2e/specs/**`) or their config — the hermetic suite is additive (`e2e/hermetic/**` + its own config).
- Build gate (whole solution): `dotnet build src/Arius.slnx -c Release` → 0 errors. In-process gate: `dotnet test --project src/Arius.Api.Integration.Tests/Arius.Api.Integration.Tests.csproj` → all green (currently 14). Hermetic browser gate: `cd src/Arius.Web && npm run e2e:hermetic` → green.
- Web project structure uses `.slnx`; central package management (no inline package versions in `.csproj`).
- Commit messages end with the repo's `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>` trailer.

## File structure

**New (server):**
- `src/Arius.Api/AriusApiHost.cs` — `AddAriusApi()` / `MapAriusApi()` extension methods (extracted from `Program.cs`).
- `src/Arius.Api.Testing/Arius.Api.Testing.csproj` — executable Web host (refs `Arius.Api`, `Arius.Core`).
- `src/Arius.Api.Testing/Program.cs` — injects scripted composer + `ScenarioRegistry` + `ScenarioGate`, calls `AddAriusApi()`/`MapAriusApi()`, maps control endpoints.
- `src/Arius.Api.Testing/ScriptedStorageCostEstimator.cs` — deterministic `IStorageCostEstimator` (replicates `FakeStorageCostEstimator`; no test-framework deps).
- `src/Arius.Api.Testing/ScenarioGate.cs` — per-repo release gate for deterministic run completion.
- `src/Arius.Api.Testing/TestingControlEndpoints.cs` — `POST /api/testing/{reset,seed-repo,scenario,release/{repoId}}`.
- Moved into `src/Arius.Api.Testing/` (namespace → `Arius.Api.Testing`): `ScenarioRegistry.cs`, `ScriptedRepositoryCoreComposer.cs`, `ScriptedArchiveHandler.cs`, `ScriptedRestoreHandler.cs`, `NotConfiguredHandlers.cs`, `CanonicalScenarios.cs`.

**Modified (server):**
- `src/Arius.Api/Program.cs` — collapses to `builder.AddAriusApi(); … app.MapAriusApi();`.
- `src/Arius.slnx` — add the new project under `/Tests/`.
- `src/Arius.Api.Integration.Tests/*` — reference `Arius.Api.Testing`; delete the 6 moved harness files; `AriusApiFactory` sources the scripted types from the new namespace.
- `src/Arius.Api/AppData/AppDatabase.cs` — add `ResetAll()`.
- Cleanups: `src/Arius.Api/Contracts/JobDetailDtos.cs` (`ResumeInfo.From`), `src/Arius.Api/Hubs/JobsHub.cs` + `src/Arius.Api/Endpoints/JobEndpoints.cs` (use it), `src/Arius.Api/Hubs/{Archive,Restore}Forwarders.cs` + `src/Arius.Api/Jobs/JobRunner.cs` (prune dead `sink.Log(…, "meta"/"info")`).

**New (web):**
- `src/Arius.Web/playwright.hermetic.config.ts`, `package.json` (`e2e:hermetic` script).
- `src/Arius.Web/e2e/hermetic/support/{control.ts,fixtures.ts,global-setup.ts}`.
- `src/Arius.Web/e2e/hermetic/specs/{jobs-live-update,cost-reattach,rehydrating-reattach,single-active-job}.spec.ts`.
- `.github/workflows/ci.yml` — new `web-e2e-hermetic` job (no Azure secrets).

---

## Task 1: Extract `AddAriusApi()` / `MapAriusApi()` from `Program.cs` (production-only refactor)

**Files:**
- Create: `src/Arius.Api/AriusApiHost.cs`
- Modify: `src/Arius.Api/Program.cs`

**Interfaces:**
- Produces: `AriusApiHost.AddAriusApi(this WebApplicationBuilder) : WebApplicationBuilder` and `AriusApiHost.MapAriusApi(this WebApplication) : WebApplication`. The composer is registered via `TryAddSingleton<IRepositoryCoreComposer, AzureRepositoryCoreComposer>()` so a pre-registered scripted composer wins.

- [ ] **Step 1: Create `AriusApiHost.cs`** with the composition lifted verbatim from `Program.cs` (only change: `AddSingleton` → `TryAddSingleton` for the composer).

`src/Arius.Api/AriusApiHost.cs`:
```csharp
using System.Text.Json;
using Arius.Api.AppData;
using Arius.Api.Composition;
using Arius.Api.Endpoints;
using Arius.Api.Hubs;
using Arius.AzureBlob;
using Arius.Core.Shared;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Arius.Api;

/// <summary>Shared composition for the Arius.Api pipeline, reused by the production host (Program.cs) and the
/// out-of-process Testing host (Arius.Api.Testing). The Core seam — <see cref="IRepositoryCoreComposer"/> — is
/// registered with <c>TryAdd</c>, so a host that pre-registers a scripted composer wins without any environment
/// branch here (mirrors the CliHarness "inject the factory" pattern).</summary>
public static class AriusApiHost
{
    public static WebApplicationBuilder AddAriusApi(this WebApplicationBuilder builder)
    {
        var dbPath = builder.Configuration["Arius:AppDbPath"]
                     ?? Path.Combine(builder.Environment.ContentRootPath, ".appstate", "arius-app.sqlite");
        var keysDir = builder.Configuration["Arius:DataProtectionKeysPath"]
                      ?? Path.Combine(Path.GetDirectoryName(dbPath)!, "keys");
        Directory.CreateDirectory(keysDir);

        builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysDir));
        builder.Services.AddSingleton(new AppDatabase(dbPath));
        builder.Services.AddSingleton<SecretProtector>();
        builder.Services.AddAzureBlobStorage();
        builder.Services.TryAddSingleton<IRepositoryCoreComposer, AzureRepositoryCoreComposer>();
        builder.Services.AddSingleton<RepositoryProviderRegistry>();
        builder.Services.AddSingleton<Arius.Api.Jobs.RestoreApprovalRegistry>();
        builder.Services.AddSingleton<Arius.Api.Jobs.JobStateRegistry>();
        builder.Services.AddSingleton<Arius.Api.Jobs.JobRunner>();
        builder.Services.AddHostedService<Arius.Api.Jobs.SchedulerService>();
        builder.Services.AddHostedService<Arius.Api.Jobs.RehydrationPollingService>();

        builder.Services.AddSignalR()
            .AddJsonProtocol(o => o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

        builder.Services.AddCors(options => options.AddPolicy("web", policy =>
            policy.WithOrigins("http://localhost:4200")
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials()
                  .WithExposedHeaders("X-Arius-Version")));

        return builder;
    }

    public static WebApplication MapAriusApi(this WebApplication app)
    {
        app.UseCors("web");
        app.UseDefaultFiles();
        app.UseStaticFiles();

        var api = app.MapGroup("/api");
        api.AddEndpointFilter(async (ctx, next) =>
        {
            ctx.HttpContext.Response.Headers["X-Arius-Version"] = AriusVersion.Display;
            return await next(ctx);
        });

        api.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        api.MapAccountEndpoints();
        api.MapRepositoryEndpoints();
        api.MapBrowseEndpoints();
        api.MapJobEndpoints();
        api.MapFilesystemEndpoints();
        app.MapHub<JobsHub>("/hubs/arius");
        app.MapFallbackToFile("index.html");

        return app;
    }
}
```

- [ ] **Step 2: Slim `Program.cs`** to call the extensions (keep Serilog bootstrap + the `public partial class Program` WAF hook).

Replace `src/Arius.Api/Program.cs` body between `builder.Host.UseSerilog();` and `app.Run();` — the whole services + mapping block (`Program.cs:24-90`) — with:
```csharp
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    builder.AddAriusApi();
    var app = builder.Build();
    app.MapAriusApi();

    Log.Information("Arius.Api {Version} starting", AriusVersion.Display);
    app.Run();
```
Remove the now-unused `using Arius.Api.AppData; using Arius.Api.Composition; using Arius.Api.Endpoints; using Arius.Api.Hubs; using Arius.AzureBlob; using System.Text.Json; using Microsoft.AspNetCore.DataProtection;` — keep `using Arius.Api; using Arius.Core.Shared; using Serilog; using Serilog.Events;`. Keep the `try/catch/finally` Serilog wrapper and `public partial class Program { }`.

- [ ] **Step 3: Build**

Run: `dotnet build src/Arius.Api/Arius.Api.csproj -c Release`
Expected: 0 errors.

- [ ] **Step 4: Verify the Api still boots** (no behaviour change)

Run: `dotnet test --project src/Arius.Api.Integration.Tests/Arius.Api.Integration.Tests.csproj`
Expected: all green (14). The integration tests boot `WebApplicationFactory<Program>` — this proves the extracted pipeline is intact and `HealthSmokeTests` still passes.

- [ ] **Step 5: Commit**
```bash
git add src/Arius.Api/AriusApiHost.cs src/Arius.Api/Program.cs
git commit -m "refactor(api): extract AddAriusApi()/MapAriusApi() so a test host can reuse the pipeline (TryAdd composer seam)"
```

---

## Task 2: Create the `Arius.Api.Testing` executable host + move the harness into it

**Files:**
- Create: `src/Arius.Api.Testing/Arius.Api.Testing.csproj`, `src/Arius.Api.Testing/Program.cs`, `src/Arius.Api.Testing/ScriptedStorageCostEstimator.cs`, `src/Arius.Api.Testing/ScenarioGate.cs`
- Move (test project → new project, namespace `Arius.Api.Testing`): `ScenarioRegistry.cs`, `ScriptedRepositoryCoreComposer.cs`, `ScriptedArchiveHandler.cs`, `ScriptedRestoreHandler.cs`, `NotConfiguredHandlers.cs`, `CanonicalScenarios.cs`
- Modify: `src/Arius.slnx`, `src/Arius.Api.Integration.Tests/Arius.Api.Integration.Tests.csproj`, `src/Arius.Api.Integration.Tests/Harness/AriusApiFactory.cs`

**Interfaces:**
- Consumes: `AriusApiHost.AddAriusApi/MapAriusApi` (Task 1), `IRepositoryCoreComposer`, `RepositoryConnection`, `PreflightMode`.
- Produces (namespace `Arius.Api.Testing`): `ScenarioRegistry` (+`Clear()`), `ScriptedRepositoryCoreComposer(ScenarioRegistry, ScenarioGate)`, `ScenarioGate`, `ScenarioContext(long RepositoryId)`, `CanonicalScenarios.RepresentativeArchive(bool gated=false)` / `.RehydratingRestore(...)`, `ScriptedStorageCostEstimator`. The host defines the executable entry point on port `ASPNETCORE_URLS`.

- [ ] **Step 1: Create the project file** — `src/Arius.Api.Testing/Arius.Api.Testing.csproj` (Web SDK ⇒ executable; no `TestingPlatformDotnetTestSupport`, so CI's test-discovery skips it — it is built, not `dotnet test`ed):
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Arius.Api\Arius.Api.csproj" />
    <ProjectReference Include="..\Arius.Core\Arius.Core.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Add the deterministic cost estimator** (replaces the `Arius.Tests.Shared` `FakeStorageCostEstimator` the composer used, so the new project has no test-framework deps) — `src/Arius.Api.Testing/ScriptedStorageCostEstimator.cs`:
```csharp
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Cost;
using Arius.Core.Shared.Storage;

namespace Arius.Api.Testing;

/// <summary>Deterministic <see cref="IStorageCostEstimator"/> for the scripted host — no pricing data, no cloud.
/// Identical arithmetic to the Core unit-test fake (kept independent so the shipped-only-in-test-host graph
/// never references Arius.Tests.Shared's TUnit/NSubstitute/Azurite deps).</summary>
public sealed class ScriptedStorageCostEstimator : IStorageCostEstimator
{
    private const double GiB = 1024.0 * 1024.0 * 1024.0;

    public string Region { get; init; } = "westeurope";

    public double StorageRate(BlobTier tier) => tier switch
    {
        BlobTier.Hot     => 0.02,
        BlobTier.Cool    => 0.01,
        BlobTier.Cold    => 0.004,
        BlobTier.Archive => 0.001,
        _                => 0.0,
    };

    public StorageCostEstimate EstimateStorageCost(IReadOnlyList<ChunkTierStatistic> storedByTier)
    {
        var tiers = storedByTier
            .Select(t => new TierStorageCost(t.Tier, t.UniqueChunks, t.StoredSize, t.StoredSize / GiB * StorageRate(t.Tier)))
            .ToList();
        return new StorageCostEstimate(Region, tiers, tiers.Sum(t => t.CostPerMonth));
    }

    public RestoreCostEstimate EstimateRestoreCost(RestoreCostRequest request)
    {
        var restoredGiB = (request.DownloadBytes + request.BytesNeedingRehydration) / GiB;
        var rehydrateGiB = request.BytesNeedingRehydration / GiB;
        return new RestoreCostEstimate
        {
            ChunksAvailable          = request.ChunksAvailable,
            ChunksAlreadyRehydrated  = request.ChunksAlreadyRehydrated,
            ChunksNeedingRehydration = request.ChunksNeedingRehydration,
            ChunksPendingRehydration = request.ChunksPendingRehydration,
            BytesNeedingRehydration  = request.BytesNeedingRehydration,
            BytesPendingRehydration  = request.BytesPendingRehydration,
            DownloadBytes            = request.DownloadBytes,
            TotalStandard            = restoredGiB,
            TotalHigh                = restoredGiB + rehydrateGiB,
            StandardWait             = TimeSpan.FromHours(15),
            HighWait                 = TimeSpan.FromHours(1),
        };
    }
}
```

- [ ] **Step 3: Add the release gate** — `src/Arius.Api.Testing/ScenarioGate.cs`:
```csharp
using System.Collections.Concurrent;

namespace Arius.Api.Testing;

/// <summary>Per-repository release latch so a scripted run can be held mid-flight (e.g. an archive kept
/// "running" so a browser test can observe it in the Active list) until a control endpoint releases it.
/// A repo with no gated scenario never awaits it.</summary>
public sealed class ScenarioGate
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource> _gates = new();

    public Task WaitForRelease(long repositoryId, CancellationToken ct)
    {
        var tcs = _gates.GetOrAdd(repositoryId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        return tcs.Task.WaitAsync(ct);
    }

    public void Release(long repositoryId)
    {
        if (_gates.TryGetValue(repositoryId, out var tcs)) tcs.TrySetResult();
    }

    public void ReleaseAll()
    {
        foreach (var tcs in _gates.Values) tcs.TrySetResult();
        _gates.Clear();
    }
}

/// <summary>The repository id of the per-repo scripted provider, so scripted handlers can key the gate.</summary>
public sealed record ScenarioContext(long RepositoryId);
```

- [ ] **Step 4: Move the 6 harness files** from `src/Arius.Api.Integration.Tests/Harness/` to `src/Arius.Api.Testing/`, changing `namespace Arius.Api.Integration.Tests.Harness;` → `namespace Arius.Api.Testing;` in each. Use `git mv` so history follows:
```bash
git mv src/Arius.Api.Integration.Tests/Harness/ScenarioRegistry.cs            src/Arius.Api.Testing/ScenarioRegistry.cs
git mv src/Arius.Api.Integration.Tests/Harness/ScriptedRepositoryCoreComposer.cs src/Arius.Api.Testing/ScriptedRepositoryCoreComposer.cs
git mv src/Arius.Api.Integration.Tests/Harness/ScriptedArchiveHandler.cs       src/Arius.Api.Testing/ScriptedArchiveHandler.cs
git mv src/Arius.Api.Integration.Tests/Harness/ScriptedRestoreHandler.cs       src/Arius.Api.Testing/ScriptedRestoreHandler.cs
git mv src/Arius.Api.Integration.Tests/Harness/NotConfiguredHandlers.cs        src/Arius.Api.Testing/NotConfiguredHandlers.cs
git mv src/Arius.Api.Integration.Tests/Harness/CanonicalScenarios.cs           src/Arius.Api.Testing/CanonicalScenarios.cs
```
Then in each moved file replace the namespace line with `namespace Arius.Api.Testing;`.

- [ ] **Step 5: Update `ScenarioRegistry.cs`** — add `Clear()`, and give the scenarios a `Gated` flag (default `false`, so existing in-process tests are unchanged). Edit the two records + add the method:
```csharp
public sealed record ArchiveScenario(IReadOnlyList<INotification> Events, ArchiveResult Result, bool Gated = false);

public sealed record RestoreScenario(
    IReadOnlyList<INotification> PreCostEvents,
    RestoreCostEstimate? CostPrompt,
    IReadOnlyList<INotification> PostApproveEvents,
    RestoreResult Result,
    bool Gated = false);
```
Add inside the `ScenarioRegistry` class:
```csharp
    public void Clear() { _archive.Clear(); _restore.Clear(); }
```

- [ ] **Step 6: Update `ScriptedRepositoryCoreComposer.cs`** — take the gate in the ctor, use the local `ScriptedStorageCostEstimator`, and register the per-repo `ScenarioContext` + gate so handlers can await it. Change the class declaration + the top of `ComposeAsync`:
```csharp
public sealed class ScriptedRepositoryCoreComposer(ScenarioRegistry scenarios, ScenarioGate gate) : IRepositoryCoreComposer
{
    public Task ComposeAsync(IServiceCollection services, RepositoryConnection connection, PreflightMode mode, CancellationToken cancellationToken)
    {
        services.AddSingleton<IStorageCostEstimator>(new ScriptedStorageCostEstimator());
        services.AddSingleton(gate);
        services.AddSingleton(new ScenarioContext(connection.RepositoryId));
        // … (the NotConfigured stand-ins + scenario overrides below are unchanged) …
```
Remove `using Arius.Tests.Shared.Fakes;` from the file.

- [ ] **Step 7: Update the scripted handlers** to honour the gate. `ScriptedArchiveHandler.cs`:
```csharp
public sealed class ScriptedArchiveHandler(IPublisher publisher, ArchiveScenario scenario, ScenarioGate gate, ScenarioContext ctx)
    : ICommandHandler<ArchiveCommand, ArchiveResult>
{
    public async ValueTask<ArchiveResult> Handle(ArchiveCommand command, CancellationToken cancellationToken)
    {
        foreach (var evt in scenario.Events)
            await publisher.Publish(evt, cancellationToken);
        if (scenario.Gated)
            await gate.WaitForRelease(ctx.RepositoryId, cancellationToken);   // held "running" until a control /release
        return scenario.Result;
    }
}
```
`ScriptedRestoreHandler.cs` — gate AFTER the post-approve events, before returning, so a browser test can hold an approved restore mid-run if needed:
```csharp
public sealed class ScriptedRestoreHandler(IPublisher publisher, RestoreScenario scenario, ScenarioGate gate, ScenarioContext ctx)
    : ICommandHandler<RestoreCommand, RestoreResult>
{
    public async ValueTask<RestoreResult> Handle(RestoreCommand command, CancellationToken cancellationToken)
    {
        foreach (var evt in scenario.PreCostEvents)
            await publisher.Publish(evt, cancellationToken);

        if (scenario.CostPrompt is not null && command.Options.ConfirmRehydration is not null)
        {
            var priority = await command.Options.ConfirmRehydration(scenario.CostPrompt, cancellationToken);
            if (priority is null)
                return scenario.Result;   // declined / timed-out — run stops
        }

        foreach (var evt in scenario.PostApproveEvents)
            await publisher.Publish(evt, cancellationToken);
        if (scenario.Gated)
            await gate.WaitForRelease(ctx.RepositoryId, cancellationToken);
        return scenario.Result;
    }
}
```

- [ ] **Step 8: Add `gated` factory params to `CanonicalScenarios.cs`** and a fast-completing archive. Change the two factory signatures + add a third:
```csharp
    public static ArchiveScenario RepresentativeArchive(bool gated = false) => new(
        Events: [ /* unchanged event list */ ],
        Result: new ArchiveResult { /* unchanged */ },
        Gated: gated);

    public static RestoreScenario RehydratingRestore(bool gated = false) => new(
        PreCostEvents: [ /* unchanged */ ],
        CostPrompt: new RestoreCostEstimate { /* unchanged */ },
        PostApproveEvents: [ /* unchanged */ ],
        Result: new RestoreResult { Success = true, FilesRestored = 3122, FilesSkipped = 0, ChunksPendingRehydration = 0, ErrorMessage = null },
        Gated: gated);

    /// <summary>A restore that PARKS at `rehydrating` after approval (post-approve result still has chunks
    /// pending), so a reattach renders the rehydration wait card + auto-resume toggle + "≈ hydrated by" (#13/#14).</summary>
    public static RestoreScenario RehydratingRestoreStaysPending() => RehydratingRestore() with
    {
        PostApproveEvents =
        [
            new RehydrationStatusEvent(Available: 145, Rehydrated: 0, NeedsRehydration: 282, Pending: 282),
        ],
        Result = new RestoreResult { Success = true, FilesRestored = 0, FilesSkipped = 0, ChunksPendingRehydration = 282, ErrorMessage = null },
    };
```
(Keep the existing `Events`/`PreCostEvents`/`CostPrompt` bodies verbatim; only the `Gated:`/`with` additions are new.)

- [ ] **Step 9: Write the host `Program.cs`** — `src/Arius.Api.Testing/Program.cs` (top-level statements; **no** `public partial class Program`, so it stays `internal` and does not clash with Arius.Api's `Program` in the integration-test project):
```csharp
using Arius.Api;
using Arius.Api.Composition;
using Arius.Api.Testing;

var builder = WebApplication.CreateBuilder(args);

// Scripted Core — registered BEFORE AddAriusApi() so its TryAddSingleton of the Azure composer is a no-op.
builder.Services.AddSingleton<ScenarioRegistry>();
builder.Services.AddSingleton<ScenarioGate>();
builder.Services.AddSingleton<IRepositoryCoreComposer, ScriptedRepositoryCoreComposer>();

builder.AddAriusApi();
var app = builder.Build();
app.MapAriusApi();
app.MapTestingControlEndpoints();
app.Run();
```
(The `AddSignalR` camelCase JSON, CORS for `:4200`, endpoints, hub, and SPA fallback all come from the shared `MapAriusApi()`, so the browser talks to this host identically to production.)

- [ ] **Step 10: Add the project to `src/Arius.slnx`** under the `/Tests/` folder:
```xml
    <Project Path="Arius.Api.Testing/Arius.Api.Testing.csproj" />
```
(Insert alongside the other `Arius.*` entries inside `<Folder Name="/Tests/">`.)

- [ ] **Step 11: Point the integration tests at the moved harness.** In `src/Arius.Api.Integration.Tests/Arius.Api.Integration.Tests.csproj` add:
```xml
    <ProjectReference Include="..\Arius.Api.Testing\Arius.Api.Testing.csproj" />
```
In every test file that used the harness (`AriusApiFactory.cs`, `ArchiveHubTests.cs`, `ArchiveScenarioTests.cs`, `RestoreCostHandshakeTests.cs`, `LifecycleScenarioTests.cs`, `ReattachScenarioTests.cs`, `RepresentationScenarioTests.cs`, `ScenarioRegistryTests.cs`, `ScriptedArchiveHandlerTests.cs`, `SingleActiveJobScenarioTests.cs`) add `using Arius.Api.Testing;` (for the moved `ScenarioRegistry`/`ScriptedRepositoryCoreComposer`/`CanonicalScenarios`/`ScenarioGate`/etc.) while keeping the existing `using Arius.Api.Integration.Tests.Harness;` (for `AriusApiFactory`, which stays put).

- [ ] **Step 11a: `AriusApiFactory` must register `ScenarioGate`.** The moved `ScriptedRepositoryCoreComposer` ctor is now `(ScenarioRegistry, ScenarioGate)`, so the in-process factory's `ConfigureServices` must provide a `ScenarioGate` too, else DI can't construct the composer. In `AriusApiFactory.cs`, alongside `services.AddSingleton(Scenarios);` add:
```csharp
            services.AddSingleton<ScenarioGate>();
```
(Order vs. `RemoveAll<IRepositoryCoreComposer>()` + `AddSingleton<..., ScriptedRepositoryCoreComposer>()` doesn't matter — all three are in the same `ConfigureServices`, which runs after `Program`'s `TryAdd` of the Azure composer, so `RemoveAll` still drops it and the scripted one wins.)

- [ ] **Step 11b: Fix direct handler construction in unit tests.** `ScriptedArchiveHandlerTests.cs` builds the handler directly; its ctor is now `(IPublisher, ArchiveScenario, ScenarioGate, ScenarioContext)`. Pass fresh instances (the gate is only awaited when `scenario.Gated`, which these tests don't set):
```csharp
        var handler = new ScriptedArchiveHandler(publisher, scenario, new ScenarioGate(), new ScenarioContext(RepositoryId: 1));
```
> Verify no other direct construction breaks: `grep -rn "new Scripted\(Archive\|Restore\)Handler\|new ScriptedRepositoryCoreComposer" src/Arius.Api.Integration.Tests` — update each to the new ctor arity. `grep -rln "Arius.Api.Integration.Tests.Harness" src/Arius.Api.Integration.Tests` should show only `AriusApiFactory` still *defines* that namespace; every other hit only *consumes* it.

- [ ] **Step 12: Build + in-process gate**

Run: `dotnet build src/Arius.slnx -c Release` → 0 errors.
Run: `dotnet test --project src/Arius.Api.Integration.Tests/Arius.Api.Integration.Tests.csproj` → all green (14). This proves the move is behaviour-preserving.

- [ ] **Step 13: Smoke the host boots out-of-process**

Run:
```bash
ASPNETCORE_URLS=http://localhost:5099 Arius__AppDbPath=$(mktemp -d)/t.sqlite \
  dotnet run --project src/Arius.Api.Testing -c Release &
sleep 8 && curl -fsS http://localhost:5099/api/health && echo " OK" ; kill %1
```
Expected: `{"status":"ok"} OK`.

- [ ] **Step 14: Commit**
```bash
git add src/Arius.Api.Testing src/Arius.slnx src/Arius.Api.Integration.Tests
git commit -m "test(api): extract scripted-Core harness into Arius.Api.Testing executable host (reused in-proc + out-of-proc)"
```

---

## Task 3: Testing control endpoints (seed repo + select scenario by name + release gate)

**Files:**
- Create: `src/Arius.Api.Testing/TestingControlEndpoints.cs`
- Modify: `src/Arius.Api/AppData/AppDatabase.cs` (`ResetAll`)

**Interfaces:**
- Consumes: `AppDatabase.{InsertAccount,InsertRepository,ResetAll}`, `SecretProtector.Protect`, `ScenarioRegistry`, `ScenarioGate`, `CanonicalScenarios`.
- Produces: `IEndpointRouteBuilder.MapTestingControlEndpoints()` mapping `POST /api/testing/{reset,seed-repo,scenario,release/{repoId:long}}`.

- [ ] **Step 1: Add `AppDatabase.ResetAll()`** (near `HasActiveJob`, ~`AppDatabase.cs:491`):
```csharp
    /// <summary>Test-only: wipes every app row (accounts/repositories/jobs/schedules/statistics) for cross-spec
    /// isolation in the hermetic browser suite. Never called by production paths.</summary>
    public void ResetAll()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM jobs; DELETE FROM schedules; DELETE FROM statistics_cache; " +
            "DELETE FROM repositories; DELETE FROM storage_accounts;";
        command.ExecuteNonQuery();
    }
```

- [ ] **Step 2: Create `TestingControlEndpoints.cs`** — `src/Arius.Api.Testing/TestingControlEndpoints.cs`:
```csharp
using Arius.Api.AppData;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Arius.Api.Testing;

/// <summary>Out-of-process control surface for the hermetic Playwright suite — the runtime equivalent of the
/// in-process AriusApiFactory.SeedRepository + factory.Scenarios.Set*. Only mapped by the Arius.Api.Testing host,
/// so production never exposes it. Jobs still START through the real hub (StartArchive/StartRestore) — this only
/// seeds a repo, picks a named scenario, and releases gated runs.</summary>
public static class TestingControlEndpoints
{
    public static IEndpointRouteBuilder MapTestingControlEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/testing");

        g.MapPost("/reset", (AppDatabase db, ScenarioRegistry scenarios, ScenarioGate gate) =>
        {
            db.ResetAll();
            scenarios.Clear();
            gate.ReleaseAll();
            return Results.Ok();
        });

        g.MapPost("/seed-repo", (SeedRepoRequest req, AppDatabase db, SecretProtector secrets) =>
        {
            var dest = req.LocalPath ?? Path.Combine(Path.GetTempPath(), $"arius-e2e-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dest);
            var accountId = db.InsertAccount("e2e-account", secrets.Protect("e2e-key"));
            var repoId = db.InsertRepository(
                alias: req.Alias ?? "e2e",
                container: req.Container ?? "e2e-container",
                accountId: accountId,
                localPath: dest,
                defaultTier: req.DefaultTier ?? "Archive",
                encryptedPassphrase: secrets.Protect("passphrase"));
            return Results.Ok(new { repoId, localPath = dest });
        });

        g.MapPost("/scenario", (ScenarioRequest req, ScenarioRegistry scenarios) =>
        {
            switch (req.Name)
            {
                case "representativeArchive":
                    scenarios.SetArchive(req.RepoId, CanonicalScenarios.RepresentativeArchive(gated: req.Gated));
                    break;
                case "rehydratingRestore":
                    scenarios.SetRestore(req.RepoId, CanonicalScenarios.RehydratingRestore(gated: req.Gated));
                    break;
                case "rehydratingRestoreStaysPending":
                    scenarios.SetRestore(req.RepoId, CanonicalScenarios.RehydratingRestoreStaysPending());
                    break;
                default:
                    return Results.BadRequest(new { error = $"Unknown scenario '{req.Name}'." });
            }
            return Results.Ok();
        });

        g.MapPost("/release/{repoId:long}", (long repoId, ScenarioGate gate) => { gate.Release(repoId); return Results.Ok(); });

        return app;
    }

    private sealed record SeedRepoRequest(string? Alias, string? Container, string? LocalPath, string? DefaultTier);
    private sealed record ScenarioRequest(long RepoId, string Name, bool Gated = false);
}
```

- [ ] **Step 3: Build**

Run: `dotnet build src/Arius.Api.Testing/Arius.Api.Testing.csproj -c Release` → 0 errors.

- [ ] **Step 4: Verify the control surface end-to-end** (boot host, seed, pick scenario)

Run:
```bash
DB=$(mktemp -d)/t.sqlite
ASPNETCORE_URLS=http://localhost:5099 Arius__AppDbPath=$DB dotnet run --project src/Arius.Api.Testing -c Release &
sleep 8
curl -fsS -XPOST http://localhost:5099/api/testing/reset
RID=$(curl -fsS -XPOST http://localhost:5099/api/testing/seed-repo -H 'content-type: application/json' -d '{}' | python3 -c 'import sys,json;print(json.load(sys.stdin)["repoId"])')
curl -fsS -XPOST http://localhost:5099/api/testing/scenario -H 'content-type: application/json' -d "{\"repoId\":$RID,\"name\":\"rehydratingRestore\"}" && echo " scenario-set"
curl -fsS http://localhost:5099/api/repos | python3 -c 'import sys,json;print("repos:",len(json.load(sys.stdin)))'
kill %1
```
Expected: `reset` 200, a numeric `repoId`, `scenario-set`, `repos: 1`.

- [ ] **Step 5: Commit**
```bash
git add src/Arius.Api.Testing/TestingControlEndpoints.cs src/Arius.Api/AppData/AppDatabase.cs
git commit -m "test(api): Testing-host control endpoints — seed repo, select named scenario, release gate"
```

---

## Task 4: Hermetic Playwright config + support (no Azure)

**Files:**
- Create: `src/Arius.Web/playwright.hermetic.config.ts`, `src/Arius.Web/e2e/hermetic/support/control.ts`, `src/Arius.Web/e2e/hermetic/support/fixtures.ts`, `src/Arius.Web/e2e/hermetic/support/global-setup.ts`, `src/Arius.Web/e2e/hermetic/specs/smoke.spec.ts`
- Modify: `src/Arius.Web/package.json`

**Interfaces:**
- Produces: `npm run e2e:hermetic`; a `test` fixture exposing `control` (`reset()`, `seedRepo()`, `scenario()`, `release()`) that talks to `/api/testing/*` through the ng proxy.

- [ ] **Step 1: Hermetic config** — `src/Arius.Web/playwright.hermetic.config.ts` (boots `Arius.Api.Testing` on `:5080`, ng serve on `:4200` with the existing proxy; no `ARIUS_E2E_*`):
```ts
import { defineConfig, devices } from '@playwright/test';

const state = new URL('./e2e/hermetic/.state/', import.meta.url).pathname;

// Hermetic browser suite: real Arius.Api pipeline, SCRIPTED Arius.Core (Arius.Api.Testing host) — no Azure.
export default defineConfig({
  testDir: './e2e/hermetic/specs',
  globalSetup: './e2e/hermetic/support/global-setup.ts',
  workers: 1,
  fullyParallel: false,
  timeout: 60_000,
  reporter: process.env.CI ? [['github'], ['html', { open: 'never', outputFolder: 'e2e/hermetic/playwright-report' }]] : 'list',
  use: { baseURL: 'http://localhost:4200', trace: 'on-first-retry' },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: [
    {
      command: 'dotnet run --project ../Arius.Api.Testing -c Release',
      url: 'http://localhost:5080/api/health',
      timeout: 120_000,
      reuseExistingServer: false,
      env: {
        ASPNETCORE_URLS: 'http://localhost:5080',
        Arius__AppDbPath: `${state}arius-hermetic.sqlite`,
        Arius__DataProtectionKeysPath: `${state}keys`,
      },
    },
    {
      command: 'npm start',
      url: 'http://localhost:4200',
      timeout: 120_000,
      reuseExistingServer: !process.env.CI,
    },
  ],
});
```

- [ ] **Step 2: Control client** — `src/Arius.Web/e2e/hermetic/support/control.ts`:
```ts
import { APIRequestContext } from '@playwright/test';

/** Thin client for the Arius.Api.Testing control endpoints (reached through the ng proxy at /api/testing). */
export class Control {
  constructor(private readonly request: APIRequestContext) {}

  async reset(): Promise<void> { await this.request.post('/api/testing/reset'); }

  async seedRepo(body: { alias?: string; defaultTier?: string } = {}): Promise<number> {
    const res = await this.request.post('/api/testing/seed-repo', { data: body });
    return (await res.json()).repoId as number;
  }

  async scenario(repoId: number, name: string, gated = false): Promise<void> {
    await this.request.post('/api/testing/scenario', { data: { repoId, name, gated } });
  }

  async release(repoId: number): Promise<void> { await this.request.post(`/api/testing/release/${repoId}`); }
}
```

- [ ] **Step 3: Fixture** — `src/Arius.Web/e2e/hermetic/support/fixtures.ts` (resets before each spec so counts/lists are isolated):
```ts
import { test as base } from '@playwright/test';
import { Control } from './control';

export const test = base.extend<{ control: Control }>({
  control: async ({ request }, use) => {
    const control = new Control(request);
    await control.reset();
    await use(control);
  },
});

export { expect } from '@playwright/test';
```

- [ ] **Step 4: Global setup** — `src/Arius.Web/e2e/hermetic/support/global-setup.ts` (just wait for the host; no Azure seeding):
```ts
import { request } from '@playwright/test';

export default async function globalSetup(): Promise<void> {
  const ctx = await request.newContext({ baseURL: 'http://localhost:5080' });
  for (let i = 0; i < 60; i++) {
    try { if ((await ctx.get('/api/health')).ok()) { await ctx.dispose(); return; } } catch { /* not up yet */ }
    await new Promise(r => setTimeout(r, 1000));
  }
  await ctx.dispose();
  throw new Error('Arius.Api.Testing did not become healthy on :5080');
}
```

- [ ] **Step 5: Smoke spec** — `src/Arius.Web/e2e/hermetic/specs/smoke.spec.ts` (proves the harness wires end-to-end):
```ts
import { test, expect } from '../support/fixtures';

test('hermetic host serves the app and seeds a repo', async ({ page, control }) => {
  const repoId = await control.seedRepo({ alias: 'smoke' });
  expect(repoId).toBeGreaterThan(0);
  await page.goto('/');
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
});
```

- [ ] **Step 6: Add the npm script** — in `src/Arius.Web/package.json` `scripts`, after `"e2e:ui"`:
```json
    "e2e:hermetic": "playwright test -c playwright.hermetic.config.ts",
```

- [ ] **Step 7: Run the smoke spec**

Run: `cd src/Arius.Web && npm run e2e:hermetic`
Expected: 1 passed. (Playwright boots both servers; global-setup waits for `/api/health`; the fixture resets; the spec seeds a repo and loads the SPA.)

- [ ] **Step 8: Commit**
```bash
cd "$(git rev-parse --show-toplevel)"
git add src/Arius.Web/playwright.hermetic.config.ts src/Arius.Web/e2e/hermetic src/Arius.Web/package.json
git commit -m "test(web): hermetic Playwright config + control-endpoint fixtures (scripted Api, no Azure)"
```

---

## Task 5: Spec #7 — `/jobs` list live-updates when a job finishes

**Files:**
- Create: `src/Arius.Web/e2e/hermetic/specs/jobs-live-update.spec.ts`

**Background:** A gated archive stays "running" until the test releases it, so the browser can observe the row in **Active** and then watch it leave live (Plan 3b Task 3: `jobDone` → `reload()`).

- [ ] **Step 1: Write the spec** — `src/Arius.Web/e2e/hermetic/specs/jobs-live-update.spec.ts`:
```ts
import { test, expect } from '../support/fixtures';

test('a finishing archive leaves Active live and the running chip drops (#7)', async ({ page, control }) => {
  const repoId = await control.seedRepo({ alias: 'live' });
  await control.scenario(repoId, 'representativeArchive', /* gated */ true);

  // Start the archive from the repo page (drives the real StartArchive hub call).
  await page.goto(`/repos/${repoId}`);
  await page.getByTestId('btn-archive').click();
  await page.getByTestId('drawer-start').click();

  // It shows up as Active on /jobs; the running chip counts it.
  await page.goto('/jobs');
  const active = page.getByTestId('jobs-active');
  await expect(active.getByTestId('job-row')).toHaveCount(1);
  await expect(page.getByText('1 running')).toBeVisible();

  // Release the gate → the archive completes → jobDone → the list re-fetches.
  await control.release(repoId);

  await expect(active.getByTestId('job-row')).toHaveCount(0);          // left Active
  await expect(active.getByText('No active jobs.')).toBeVisible();
  await expect(page.getByText('0 running')).toBeVisible();             // chip updated
  await expect(page.getByTestId('jobs-history').getByTestId('job-row')).toHaveCount(1);  // now in history
});
```

- [ ] **Step 2: Run**

Run: `cd src/Arius.Web && npx playwright test -c playwright.hermetic.config.ts jobs-live-update`
Expected: 1 passed.

- [ ] **Step 3: Commit**
```bash
cd "$(git rev-parse --show-toplevel)"
git add src/Arius.Web/e2e/hermetic/specs/jobs-live-update.spec.ts
git commit -m "test(web): hermetic e2e — /jobs list live-updates on completion (#7)"
```

---

## Task 6: Spec #2 (cost modal on reattach) + #13/#14 (rehydrating reattach)

**Files:**
- Create: `src/Arius.Web/e2e/hermetic/specs/cost-reattach.spec.ts`, `src/Arius.Web/e2e/hermetic/specs/rehydrating-reattach.spec.ts`

**Background:** A `rehydratingRestore` parks at `awaiting-cost` on the ConfirmRehydration handshake (naturally stuck ≤15 min) — perfect for #2. `rehydratingRestoreStaysPending` parks at `rehydrating` with persisted resume state (JobRunner parks when the approved run still reports `ChunksPendingRehydration > 0`), so a reattach renders the wait card + auto-resume toggle (#14) + "≈ hydrated by" (#13) from Plan 3b's seeded `resume`.

- [ ] **Step 1: #2 spec** — `src/Arius.Web/e2e/hermetic/specs/cost-reattach.spec.ts`:
```ts
import { test, expect } from '../support/fixtures';

test('the cost modal renders on a fresh reattach of an awaiting-cost restore (#2)', async ({ page, control }) => {
  const repoId = await control.seedRepo({ alias: 'cost' });
  await control.scenario(repoId, 'rehydratingRestore');

  // Start a restore; it parks at awaiting-cost and pushes a live CostEstimate → modal appears.
  await page.goto(`/repos/${repoId}`);
  await page.getByTestId('btn-restore').click();
  await page.getByTestId('drawer-start').click();
  await expect(page.getByTestId('cost-modal')).toBeVisible();
  await expect(page.getByTestId('cost-approve')).toBeVisible();

  // The job is listed under "Needs your attention" with a Review-cost action.
  await page.goto('/jobs');
  const attention = page.getByTestId('jobs-needs-attention');
  await expect(attention.getByTestId('job-review-cost')).toBeVisible();

  // Reattach from the list → the job detail again surfaces the awaiting-cost cost prompt (Plan 3a/3b seeding).
  await attention.getByTestId('job-review-cost').click();
  await expect(page).toHaveURL(/\/jobs\//);
  await expect(page.getByTestId('cost-modal')).toBeVisible();          // #2: cost flows on reattach
});
```

- [ ] **Step 2: #13/#14 spec** — `src/Arius.Web/e2e/hermetic/specs/rehydrating-reattach.spec.ts`:
```ts
import { test, expect } from '../support/fixtures';

test('a rehydrating reattach shows the auto-resume toggle (#14) and hydrated-by ETA (#13)', async ({ page, control }) => {
  const repoId = await control.seedRepo({ alias: 'rehy' });
  await control.scenario(repoId, 'rehydratingRestoreStaysPending');

  // Start + approve the restore; the approved run still has chunks pending → parks at `rehydrating`.
  await page.goto(`/repos/${repoId}`);
  await page.getByTestId('btn-restore').click();
  await page.getByTestId('drawer-start').click();
  await expect(page.getByTestId('cost-modal')).toBeVisible();
  await page.getByTestId('cost-approve').click();

  // Reattach fresh (new navigation → attach() re-reads persisted resume state).
  await page.goto('/jobs');
  const active = page.getByTestId('jobs-active');
  await expect(active.getByTestId('job-row')).toHaveCount(1);
  await active.getByTestId('job-reattach').click();

  await expect(page.getByTestId('job-status')).toContainText('Rehydrating');
  const toggle = page.getByTestId('autoresume-toggle');
  await expect(toggle).toBeVisible();                                  // #14: real toggle, seeded from resume
  await expect(toggle).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByText(/hydrated by/)).toBeVisible();           // #13: ETA from persisted window
});
```

- [ ] **Step 3: Run**

Run: `cd src/Arius.Web && npx playwright test -c playwright.hermetic.config.ts cost-reattach rehydrating-reattach`
Expected: 2 passed.

> If `rehydrating-reattach` flakes because the `RehydrationPollingService` re-drives the parked job (it re-runs the scripted restore, which stays pending → stays `rehydrating`), the state is still stable; the assertion waits on the rendered card. Do not disable the poller — the stable-rehydrating behaviour is exactly #13/#14's subject.

- [ ] **Step 4: Commit**
```bash
cd "$(git rev-parse --show-toplevel)"
git add src/Arius.Web/e2e/hermetic/specs/cost-reattach.spec.ts src/Arius.Web/e2e/hermetic/specs/rehydrating-reattach.spec.ts
git commit -m "test(web): hermetic e2e — cost modal on reattach (#2) + rehydrating auto-resume/ETA (#13/#14)"
```

---

## Task 7: Spec #1 — single-active-job blocks a second start in the UI

**Files:**
- Create: `src/Arius.Web/e2e/hermetic/specs/single-active-job.spec.ts`

**Background:** With a restore parked at `awaiting-cost`, `HasActiveJob` is true, so a second `StartArchive`/`StartRestore` on the same repo throws a `HubException`. `DrawerStore.start()` catches it and sets `error`, which the drawer renders as `data-testid="start-error"` (`archive-restore-drawer.component.ts:29`).

- [ ] **Step 1: Write the spec** — `src/Arius.Web/e2e/hermetic/specs/single-active-job.spec.ts`:
```ts
import { test, expect } from '../support/fixtures';

test('a second job on a busy repo is rejected in the UI (#1, by design)', async ({ page, control }) => {
  const repoId = await control.seedRepo({ alias: 'busy' });
  await control.scenario(repoId, 'rehydratingRestore');

  // Park a restore at awaiting-cost (the repo is now busy).
  await page.goto(`/repos/${repoId}`);
  await page.getByTestId('btn-restore').click();
  await page.getByTestId('drawer-start').click();
  await expect(page.getByTestId('cost-modal')).toBeVisible();          // parked, repo busy

  // Attempt to start an archive on the SAME repo → the hub rejects; the drawer surfaces the error inline.
  await page.goto(`/repos/${repoId}`);
  await page.getByTestId('btn-archive').click();
  await page.getByTestId('drawer-start').click();
  await expect(page.getByTestId('start-error')).toBeVisible();
  await expect(page.getByTestId('start-error')).toContainText(/already running/i);
  await expect(page.getByTestId('drawer')).toBeVisible();              // drawer stays open (not silently stuck)
});
```

> Confirm the `HubException` message text at `JobsHub.StartArchive`/`StartRestore` matches `/already running/i`; if it differs (e.g. "A job is already active"), adjust the regex to the actual message.

- [ ] **Step 2: Run**

Run: `cd src/Arius.Web && npx playwright test -c playwright.hermetic.config.ts single-active-job`
Expected: 1 passed.

- [ ] **Step 3: Commit**
```bash
cd "$(git rev-parse --show-toplevel)"
git add src/Arius.Web/e2e/hermetic/specs/single-active-job.spec.ts
git commit -m "test(web): hermetic e2e — single-active-job rejection surfaced in the UI (#1)"
```

---

## Task 8: CI — `web-e2e-hermetic` job (runs on every PR, no Azure secrets)

**Files:**
- Modify: `.github/workflows/ci.yml`

- [ ] **Step 1: Add the job** (sibling to `web-unit`, after it):
```yaml
  web-e2e-hermetic:
    name: 🎭 Web e2e (hermetic, scripted Core)
    runs-on: ubuntu-latest
    defaults:
      run:
        working-directory: src/Arius.Web
    steps:
      - name: 🛎️ Checkout
        uses: actions/checkout@v7
      - name: ⚙️ Setup .NET
        uses: actions/setup-dotnet@v5
        with:
          dotnet-version: "10.0.x"
      - name: ⚙️ Setup Node
        uses: actions/setup-node@v6
        with:
          node-version: "22"
          cache: npm
          cache-dependency-path: src/Arius.Web/package-lock.json
      - name: 📦 Install dependencies
        run: npm ci
      - name: 🌐 Install Playwright browser
        run: npx playwright install --with-deps chromium
      - name: 🎭 Run hermetic e2e
        run: npm run e2e:hermetic
      - name: ⬆️ Upload Playwright report
        if: ${{ !cancelled() }}
        uses: actions/upload-artifact@v7
        with:
          name: playwright-report-hermetic
          path: src/Arius.Web/e2e/hermetic/playwright-report
          retention-days: 7
```
(No `if:` gate and no `ARIUS_E2E_*`/Azure env — it is fully hermetic, so it runs on every push/PR unlike the Azure `e2e` job.)

- [ ] **Step 2: Validate YAML**

Run: `python3 -c "import yaml; d=yaml.safe_load(open('.github/workflows/ci.yml')); print(list(d['jobs'].keys()))"`
Expected: `['test', 'e2e', 'web-unit', 'web-e2e-hermetic']`.

- [ ] **Step 3: Commit**
```bash
git add .github/workflows/ci.yml
git commit -m "ci: run the hermetic web e2e suite on every PR (scripted Core, no Azure)"
```

---

## Task 9: Carried cleanups (fold the Plan 3b deferrals now that the code is touched)

**Files:**
- Modify: `src/Arius.Api/Contracts/JobDetailDtos.cs`, `src/Arius.Api/Hubs/JobsHub.cs`, `src/Arius.Api/Endpoints/JobEndpoints.cs`
- Modify: `src/Arius.Api/Hubs/ArchiveForwarders.cs`, `src/Arius.Api/Hubs/RestoreForwarders.cs`, `src/Arius.Api/Jobs/JobRunner.cs`

**Background:** Three deferrals named in Plan 3b's self-review. `ToResumeInfo` is duplicated verbatim in `JobsHub.cs:111-112` and `JobEndpoints.cs:114-115`. `JobSink.Log` is a no-op for `"meta"/"info"` (only `"warn"/"error"` capture warnings — `JobSink.cs:33-39`), so those log calls are dead string-building. (The `ScriptedRestoreHandler` distinct-declined-result concern is already resolved: JobRunner ignores the handler's result on the decline branch — it calls `CompleteJob("cancelled")` directly — so no change is needed there.)

- [ ] **Step 1: Add `ResumeInfo.From`** — `src/Arius.Api/Contracts/JobDetailDtos.cs`, change the record to carry the mapping:
```csharp
public sealed record ResumeInfo(bool AutoResume, System.DateTimeOffset RehydrationStartedAt, double RehydrationWindowHours)
{
    /// <summary>Maps the persisted restore-resume state to the wire DTO (null-safe). Shared by JobsHub + JobEndpoints.</summary>
    public static ResumeInfo? From(Arius.Api.Jobs.RestoreResumeState? r) =>
        r is null ? null : new ResumeInfo(r.AutoResume, r.RehydrationStartedAt, r.RehydrationWindow.TotalHours);
}
```
(Use the fully-qualified `RestoreResumeState` type name matching its namespace; confirm with `grep -rn "record RestoreResumeState\|class RestoreResumeState" src/Arius.Api`.)

- [ ] **Step 2: Use it + delete the dupes.** In `src/Arius.Api/Hubs/JobsHub.cs`: replace the two `ToResumeInfo(...)` call sites (`:96`, `:104`) with `ResumeInfo.From(...)` and delete the private `ToResumeInfo` method (`:111-112`). Same in `src/Arius.Api/Endpoints/JobEndpoints.cs` (`:51`, `:61` call sites; delete `:114-115`).

- [ ] **Step 3: Prune dead `sink.Log(…, "meta"/"info")` calls.** Remove these no-op statements (keep every `"warn"`/`"error"` call — they feed `warningCount`):
  - `ArchiveForwarders.cs`: `:17` (`"info"`), `:42` (`"meta"`), `:81` (`"info"`).
  - `RestoreForwarders.cs`: `:13` (`"meta"`), `:60` (`"meta"`). Keep `:42` (severity is conditional `"warn"`/`"info"` and still captures warnings — leave it).
  - `JobRunner.cs`: `:75` (`"meta"`), `:80` (`"meta"`), `:175` (`"meta"`), `:225` (`"info"`). Keep `:231`/`:272`/`:294` (`"warn"`).

  > Verify after: `grep -rn 'sink.Log(' src/Arius.Api --include=*.cs | grep '"meta"\|"info"'` returns only the conditional `RestoreForwarders.cs:42` line. If a pruned line was the only statement in a forwarder's `Handle`, leave a `return ValueTask.CompletedTask;` (or keep the method body valid) — confirm each forwarder still compiles.

- [ ] **Step 4: Build + full in-process suite**

Run: `dotnet build src/Arius.slnx -c Release` → 0 errors.
Run: `dotnet test --project src/Arius.Api.Integration.Tests/Arius.Api.Integration.Tests.csproj` → all green.

- [ ] **Step 5: Commit**
```bash
git add src/Arius.Api/Contracts/JobDetailDtos.cs src/Arius.Api/Hubs/JobsHub.cs src/Arius.Api/Endpoints/JobEndpoints.cs src/Arius.Api/Hubs/ArchiveForwarders.cs src/Arius.Api/Hubs/RestoreForwarders.cs src/Arius.Api/Jobs/JobRunner.cs
git commit -m "refactor(api): dedupe ToResumeInfo → ResumeInfo.From + prune dead meta/info sink.Log calls"
```

---

## Self-review

**Spec coverage:**
- Harness reachable out-of-process → Tasks 1 (`AddAriusApi`/`MapAriusApi` + `TryAdd` seam) + 2 (`Arius.Api.Testing` exe, moved harness, `dotnet run` smoke).
- `Testing`-gated control surface → Task 3 (`/api/testing/{reset,seed-repo,scenario,release}`), reused by both in-proc factory and out-of-proc specs.
- Hermetic Playwright boot (no Azure) → Task 4 (config + control fixtures + smoke).
- #7 → Task 5; #2 → Task 6; #13/#14 → Task 6; #1 → Task 7.
- CI on every PR → Task 8.
- Carried cleanups (`ToResumeInfo` dedupe, dead `sink.Log`) → Task 9; the `ScriptedRestoreHandler` declined-result deferral is explicitly a no-op (JobRunner ignores the result on decline).
- Production isolation (no test ref / no env branch) → Task 1's `TryAdd` + Task 2's host pre-registration; verified by Task 2 Step 12 (whole-solution Release build with Arius.Api unchanged).

**Placeholder scan:** No TBD/TODO. The `>` notes are verification/adaptation points, each naming an exact command (confirm the harness namespace split; confirm the `HubException` message regex; confirm `RestoreResumeState`'s namespace; confirm the pruned forwarder bodies compile; the poller/rehydrating-stability note).

**Type consistency:**
- `ScriptedRepositoryCoreComposer(ScenarioRegistry, ScenarioGate)`, `ScriptedArchiveHandler(IPublisher, ArchiveScenario, ScenarioGate, ScenarioContext)`, `ScriptedRestoreHandler(IPublisher, RestoreScenario, ScenarioGate, ScenarioContext)` — the composer registers `ScenarioGate` + `ScenarioContext` into the per-repo provider (Task 2 Step 6), matching the handlers' ctors (Step 7).
- `CanonicalScenarios.RepresentativeArchive(bool gated=false)` / `.RehydratingRestore(bool gated=false)` / `.RehydratingRestoreStaysPending()` — signatures match the control endpoint's `scenario` switch (Task 3) and the specs' `control.scenario(repoId, name, gated)` (Tasks 5–7).
- `Control.{reset,seedRepo,scenario,release}` (Task 4) ↔ `/api/testing/{reset,seed-repo,scenario,release/{repoId}}` (Task 3).
- Selectors used in specs are all present in the components read during planning: `btn-archive`/`btn-restore` (`repo-detail.component.ts:35-36`), `drawer`/`drawer-start`/`start-error` (`archive-restore-drawer.component.ts:16/70/29`), `jobs-active`/`jobs-needs-attention`/`jobs-history`/`job-row`/`job-reattach`/`job-review-cost`/`job-status` (`jobs.component.ts`), `cost-modal`/`cost-approve`/`autoresume-toggle` (`job-detail.component.ts:230/272/112`).

**Deferred / accepted:**
- Making the existing **real-Azure** specs hermetic is out of scope — this plan is additive (`e2e/hermetic/**`), leaving `e2e/specs/**` and their Azure gate untouched.
- Excluding `Arius.Api.Testing` from a production `publish` is unnecessary: the production `Arius.Api` project does not reference it (Task 1's `TryAdd` seam means only the separate host wires the scripted composer), so it is never in the Api's output.
