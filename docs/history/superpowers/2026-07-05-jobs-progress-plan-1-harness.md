# Jobs progress — Plan 1: scripted-fake-Core test harness

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up an in-process test harness that runs `Arius.Api` (hub + endpoints + `JobRunner` + `JobSink` + SignalR) against a *scripted fake* `Arius.Core`, so any progress / tier / cost / rehydration / warning scenario can be driven deterministically and offline.

**Architecture:** The per-repository Core composition inside `RepositoryProviderRegistry` is extracted behind a new `IRepositoryCoreComposer` seam (production = real `AddAzureBlobStorage()+AddArius()`; unchanged behavior). A new `Arius.Api.Integration.Tests` project boots the Api with `WebApplicationFactory<Program>` and swaps in a `ScriptedRepositoryCoreComposer` whose `ScriptedArchive/RestoreHandler`s publish the **real** Core `INotification` events from a per-repo `ScenarioRegistry` and drive the `ConfirmRehydration` cost handshake. The real Mediator pipeline, forwarders, `JobSink`, and SignalR run unchanged.

**Tech Stack:** .NET 10, ASP.NET Core minimal API + SignalR, Martin Othamar `Mediator` (source-generated), TUnit + Shouldly + NSubstitute, `WebApplicationFactory` (`Microsoft.AspNetCore.Mvc.Testing`), `Microsoft.AspNetCore.SignalR.Client`.

## Global Constraints

- **No `Arius.Core` changes.** Every seam is Api-side. If an `internal`-visibility wall is hit, flag it rather than widening Core.
- **Production behavior must be byte-identical** after the Task-1 refactor — it is a pure extraction.
- Central Package Management: add packages with `dotnet add … package` (never hand-edit version XML); versions land in `Directory.Packages.props`.
- Target framework `net10.0`; `Nullable` + `ImplicitUsings` enabled (match `Arius.Api.Tests.csproj`).
- TUnit test style: `[Test] public async Task Name()` with `await Assert.That(x).IsEqualTo(y)` (see `src/Arius.Api.Tests/Jobs/JobSinkAggregateTests.cs`).
- **Scope note vs the design doc:** the design listed a `Testing`-gated `/test/scenario` control endpoint under the harness. It is only consumed by out-of-process Playwright e2e, so it is **deferred to Plan 3**; Plan 1 configures scenarios in-process via DI, keeping production `Program.cs` free of test types.

---

## File structure

**Production (`src/Arius.Api/`) — Task 1 only:**
- Create `Composition/IRepositoryCoreComposer.cs` — the seam interface.
- Create `Composition/AzureRepositoryCoreComposer.cs` — production impl (real blob-open + `AddAzureBlobStorage()+AddArius()`).
- Modify `Composition/RepositoryProviderRegistry.cs` — drop `IBlobServiceFactory`; call the composer.
- Modify `Program.cs` — register the composer; append `public partial class Program { }`.

**Test project (`src/Arius.Api.Integration.Tests/`) — all new:**
- `Arius.Api.Integration.Tests.csproj`
- `Harness/AriusApiFactory.cs` — `WebApplicationFactory<Program>` + repo seeding.
- `Harness/ScenarioRegistry.cs` — per-repo scripted scenarios + scenario record types.
- `Harness/ScriptedRepositoryCoreComposer.cs` — registers scripted handlers + `FakeStorageCostEstimator`.
- `Harness/ScriptedArchiveHandler.cs`, `Harness/ScriptedRestoreHandler.cs`.
- `Harness/CanonicalScenarios.cs` — reusable representative archive/restore scripts (used by Plans 2 & 3 too).
- `HealthSmokeTests.cs`, `ArchiveScenarioTests.cs`, `ArchiveHubTests.cs`, `RestoreCostHandshakeTests.cs`, `FidelityTests.cs`.

---

## Task 1: Extract the `IRepositoryCoreComposer` seam (pure refactor)

**Files:**
- Create: `src/Arius.Api/Composition/IRepositoryCoreComposer.cs`
- Create: `src/Arius.Api/Composition/AzureRepositoryCoreComposer.cs`
- Modify: `src/Arius.Api/Composition/RepositoryProviderRegistry.cs:45-56` (ctor), `:115-140` (`BuildAsync`)
- Modify: `src/Arius.Api/Program.cs:37-38`, end-of-file

**Interfaces:**
- Produces: `public interface IRepositoryCoreComposer { Task ComposeAsync(IServiceCollection services, RepositoryConnection connection, PreflightMode mode, CancellationToken cancellationToken); }` — Task 5's `ScriptedRepositoryCoreComposer` implements it.
- Consumes: existing public `RepositoryConnection` (`Arius.Api.AppData`) and `PreflightMode` (`Arius.Core.Shared.Storage`).

This is a behavior-preserving extraction: production still runs `AddAzureBlobStorage()+AddArius()`, just from the composer. Its gate is "build clean + existing tests still green"; the swap is exercised in Task 6.

- [ ] **Step 1: Create the interface**

`src/Arius.Api/Composition/IRepositoryCoreComposer.cs`:

```csharp
using Arius.Api.AppData;
using Arius.Core.Shared.Storage;

namespace Arius.Api.Composition;

/// <summary>
/// Registers the per-repository Arius.Core service graph (command/query handlers + the storage
/// services they depend on) into a freshly-built per-job/read <see cref="IServiceCollection"/>.
/// Production wires the real Azure-backed Core (<see cref="AzureRepositoryCoreComposer"/>); tests can
/// swap in a scripted fake without touching Arius.Core.
///
/// The registry always runs <c>AddMediator()</c> itself (the generated mediator + the Api's event
/// forwarders must be composed in the Api assembly) and then calls this to add the handlers + storage.
/// </summary>
public interface IRepositoryCoreComposer
{
    Task ComposeAsync(IServiceCollection services, RepositoryConnection connection, PreflightMode mode, CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Create the production composer**

`src/Arius.Api/Composition/AzureRepositoryCoreComposer.cs`:

```csharp
using Arius.Api.AppData;
using Arius.AzureBlob;
using Arius.Core;
using Arius.Core.Shared.Storage;

namespace Arius.Api.Composition;

/// <summary>Production composer: opens the real Azure container and registers the real Arius.Core graph.
/// This is exactly the blob-open + registration that lived inline in <see cref="RepositoryProviderRegistry"/>.</summary>
public sealed class AzureRepositoryCoreComposer(IBlobServiceFactory blobServiceFactory) : IRepositoryCoreComposer
{
    public async Task ComposeAsync(IServiceCollection services, RepositoryConnection connection, PreflightMode mode, CancellationToken cancellationToken)
    {
        var blobService   = await blobServiceFactory.CreateAsync(connection.AccountName, connection.AccountKey, cancellationToken).ConfigureAwait(false);
        var blobContainer = await blobService.OpenContainerServiceAsync(connection.Container, mode, cancellationToken).ConfigureAwait(false);

        services.AddAzureBlobStorage();
        services.AddArius(blobContainer, connection.Passphrase, connection.AccountName, connection.Container);
    }
}
```

- [ ] **Step 3: Rewire the registry ctor** — replace `IBlobServiceFactory blobServiceFactory` with the composer.

In `RepositoryProviderRegistry.cs`, change the field (`:32`) and ctor (`:45-56`):

```csharp
    private readonly IRepositoryCoreComposer _coreComposer;
    private readonly ILoggerFactory      _loggerFactory;
    private readonly ILogger<RepositoryProviderRegistry> _logger;

    private readonly object _gate = new();
    private readonly Dictionary<long, Lazy<Task<ServiceProvider>>> _readProviders = new();
    private readonly Dictionary<long, ILoggerFactory> _repoLoggerFactories = new();

    public RepositoryProviderRegistry(
        AppDatabase database,
        SecretProtector secrets,
        IRepositoryCoreComposer coreComposer,
        ILoggerFactory loggerFactory)
    {
        _database           = database;
        _secrets            = secrets;
        _coreComposer       = coreComposer;
        _loggerFactory      = loggerFactory;
        _logger             = loggerFactory.CreateLogger<RepositoryProviderRegistry>();
    }
```

(Delete the old `private readonly IBlobServiceFactory _blobServiceFactory;` field and its assignment.)

- [ ] **Step 4: Rewire `BuildAsync`** — replace the inline blob-open + `AddAzureBlobStorage()+AddArius()` (`:115-140`) with a composer call:

```csharp
    private async Task<ServiceProvider> BuildAsync(long repositoryId, PreflightMode mode, JobSink jobSink, CancellationToken cancellationToken)
    {
        var connection = LoadConnection(repositoryId);

        var services = new ServiceCollection();

        // Route Core's logging to the repository's shared rolling log file (same as before).
        var repoLoggerFactory = GetOrCreateRepoLoggerFactory(repositoryId, connection.AccountName, connection.Container);
        services.AddSingleton(repoLoggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        // Per-job sink resolved by the event forwarders (auto-registered by AddMediator).
        services.AddSingleton(jobSink);

        // AddMediator() (generated in this assembly) must run here, not inside the composer.
        services.AddMediator();

        // The Arius.Core graph (handlers + storage) is composed behind an interface so tests can
        // swap in a scripted fake without touching Arius.Core.
        await _coreComposer.ComposeAsync(services, connection, mode, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Built {Mode} provider for repository {RepositoryId} ({Account}/{Container})", mode, repositoryId, connection.AccountName, connection.Container);
        return services.BuildServiceProvider();
    }
```

If the compiler flags the `Arius.AzureBlob` / `Arius.Core.Shared.Storage` usings at the top of the registry as now-unused, remove them (they moved to the composer). Keep `Arius.Core.Shared` (used by `RepositoryLocalStatePaths`).

- [ ] **Step 5: Register the composer in `Program.cs`** — after `builder.Services.AddAzureBlobStorage();` (`:37`):

```csharp
    builder.Services.AddAzureBlobStorage();
    builder.Services.AddSingleton<IRepositoryCoreComposer, AzureRepositoryCoreComposer>();
    builder.Services.AddSingleton<RepositoryProviderRegistry>();
```

- [ ] **Step 6: Make `Program` test-accessible** — append at the very end of `Program.cs` (after the `finally` block, at file scope):

```csharp

// Exposed so Arius.Api.Integration.Tests can boot the app with WebApplicationFactory<Program>.
public partial class Program { }
```

- [ ] **Step 7: Build + run existing tests (the refactor gate)**

Run: `dotnet build src/Arius.Api/Arius.Api.csproj -c Debug`
Expected: build succeeds, no warnings about unused fields.

Run: `dotnet test src/Arius.Api.Tests/Arius.Api.Tests.csproj`
Expected: all existing tests PASS (behavior unchanged).

- [ ] **Step 8: Commit**

```bash
git add src/Arius.Api/Composition/IRepositoryCoreComposer.cs src/Arius.Api/Composition/AzureRepositoryCoreComposer.cs src/Arius.Api/Composition/RepositoryProviderRegistry.cs src/Arius.Api/Program.cs
git commit -m "refactor(api): extract IRepositoryCoreComposer seam for per-repo Core composition"
```

---

## Task 2: New integration-test project + `WebApplicationFactory` + health smoke test

**Files:**
- Create: `src/Arius.Api.Integration.Tests/Arius.Api.Integration.Tests.csproj`
- Create: `src/Arius.Api.Integration.Tests/Harness/AriusApiFactory.cs`
- Create: `src/Arius.Api.Integration.Tests/HealthSmokeTests.cs`
- Modify: the solution file (`*.slnx` / `*.sln`)

**Interfaces:**
- Produces: `AriusApiFactory : WebApplicationFactory<Program>` with `public ScenarioRegistry Scenarios { get; }` and `long SeedRepository(string? localPath = null)`. Tasks 5-9 consume it. (`Scenarios` + seeding are stubbed here and completed in Tasks 3/5.)

- [ ] **Step 1: Create the csproj**

`src/Arius.Api.Integration.Tests/Arius.Api.Integration.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

	<PropertyGroup>
		<TargetFramework>net10.0</TargetFramework>
		<ImplicitUsings>enable</ImplicitUsings>
		<Nullable>enable</Nullable>
		<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>
	</PropertyGroup>

	<ItemGroup>
		<PackageReference Include="Shouldly" />
		<PackageReference Include="TUnit" />
		<PackageReference Include="NSubstitute" />
		<PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" />
		<PackageReference Include="Microsoft.AspNetCore.SignalR.Client" />
	</ItemGroup>

	<ItemGroup>
		<ProjectReference Include="..\Arius.Api\Arius.Api.csproj" />
		<ProjectReference Include="..\Arius.Tests.Shared\Arius.Tests.Shared.csproj" />
	</ItemGroup>

</Project>
```

- [ ] **Step 2: Add missing packages via CPM**

Run (adds versionless `PackageReference` above + a `<PackageVersion>` in `Directory.Packages.props`):

```bash
dotnet add src/Arius.Api.Integration.Tests package Microsoft.AspNetCore.Mvc.Testing
dotnet add src/Arius.Api.Integration.Tests package Microsoft.AspNetCore.SignalR.Client
dotnet add src/Arius.Api.Integration.Tests package NSubstitute
```

(`TUnit` and `Shouldly` versions already exist in `Directory.Packages.props` from `Arius.Api.Tests`.) If a package resolves to a version below the repo's net10.0 floor, pin it via the `package-management` skill.

- [ ] **Step 3: Add the project to the solution**

Run: `dotnet sln list` to find the solution file, then:
`dotnet sln add src/Arius.Api.Integration.Tests/Arius.Api.Integration.Tests.csproj`
Expected: "Project ... added to the solution."

- [ ] **Step 4: Write the factory (Scenarios + seeding stubbed until Tasks 3/5)**

`src/Arius.Api.Integration.Tests/Harness/AriusApiFactory.cs`:

```csharp
using Arius.Api.AppData;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Api.Integration.Tests.Harness;

/// <summary>Boots Arius.Api in-process with a throwaway SQLite app-db and (from Task 5) a scripted Core.</summary>
public sealed class AriusApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"arius-itest-{Guid.NewGuid():N}.sqlite");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Arius:AppDbPath", _dbPath);
        // Task 5 adds: register ScenarioRegistry + swap IRepositoryCoreComposer here.
    }

    /// <summary>Seeds an account + repository row (protected secrets, no real Azure) and returns the repo id.</summary>
    public long SeedRepository(string? localPath = null)
    {
        var db      = Services.GetRequiredService<AppDatabase>();
        var secrets = Services.GetRequiredService<SecretProtector>();
        var accountId = db.InsertAccount("fake-account", secrets.Protect("fake-key"));
        return db.InsertRepository(
            alias: "itest",
            container: "itest-container",
            accountId: accountId,
            localPath: localPath ?? Path.Combine(Path.GetTempPath(), $"arius-itest-src-{Guid.NewGuid():N}"),
            defaultTier: "Archive",
            encryptedPassphrase: secrets.Protect("passphrase"));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}
```

> If `SecretProtector` exposes `Protect` under a different name, use that name (it is the inverse of the `Unprotect` used in `RepositoryProviderRegistry.LoadConnection`). Confirm with: `grep -n "public string Protect" src/Arius.Api/AppData/*.cs`.

- [ ] **Step 5: Write the health smoke test**

`src/Arius.Api.Integration.Tests/HealthSmokeTests.cs`:

```csharp
using System.Net;
using Arius.Api.Integration.Tests.Harness;

namespace Arius.Api.Integration.Tests;

public class HealthSmokeTests
{
    [Test]
    public async Task Health_endpoint_returns_ok()
    {
        await using var factory = new AriusApiFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }
}
```

- [ ] **Step 6: Run it**

Run: `dotnet test src/Arius.Api.Integration.Tests/Arius.Api.Integration.Tests.csproj`
Expected: PASS. (The app boots against the temp SQLite db; the real `AzureRepositoryCoreComposer` is registered but never invoked because no job runs.)

- [ ] **Step 7: Commit**

```bash
git add src/Arius.Api.Integration.Tests Directory.Packages.props *.slnx *.sln
git commit -m "test(api): add Arius.Api.Integration.Tests with WebApplicationFactory harness"
```

---

## Task 3: `ScenarioRegistry` + scenario record types

**Files:**
- Create: `src/Arius.Api.Integration.Tests/Harness/ScenarioRegistry.cs`
- Create: `src/Arius.Api.Integration.Tests/ScenarioRegistryTests.cs`

**Interfaces:**
- Produces:
  - `ArchiveScenario(IReadOnlyList<INotification> Events, ArchiveResult Result)`
  - `RestoreScenario(IReadOnlyList<INotification> PreCostEvents, RestoreCostEstimate? CostPrompt, IReadOnlyList<INotification> PostApproveEvents, RestoreResult Result)`
  - `ScenarioRegistry` with `void SetArchive(long repoId, ArchiveScenario)`, `void SetRestore(long repoId, RestoreScenario)`, `ArchiveScenario? TakeArchive(long repoId)`, `RestoreScenario? TakeRestore(long repoId)`.
- Consumes: real Core types `INotification` (`Mediator`), `ArchiveResult`, `RestoreResult`, `RestoreCostEstimate`.

- [ ] **Step 1: Write the failing test**

`src/Arius.Api.Integration.Tests/ScenarioRegistryTests.cs`:

```csharp
using Arius.Api.Integration.Tests.Harness;
using Arius.Core.Features.ArchiveCommand;

namespace Arius.Api.Integration.Tests;

public class ScenarioRegistryTests
{
    [Test]
    public async Task Set_then_take_returns_the_scenario_once_per_repo()
    {
        var registry = new ScenarioRegistry();
        var scenario = new ArchiveScenario(
            Events: [new ScanCompleteEvent(1, 100)],
            Result: NewArchiveResult());

        registry.SetArchive(7, scenario);

        await Assert.That(registry.TakeArchive(7)).IsSameReferenceAs(scenario);
        await Assert.That(registry.TakeArchive(99)).IsNull();   // other repo unaffected
    }

    private static ArchiveResult NewArchiveResult() => new()
    {
        Success = true, FilesScanned = 1, EntriesExcluded = 0, FilesUploaded = 1, FilesDeduped = 0,
        OriginalSize = 100, IncrementalSize = 100, IncrementalStoredSize = 40, FastHashReused = 0,
        FastHashRehashed = 1, RootHash = null, SnapshotTime = DateTimeOffset.UnixEpoch,
    };
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test src/Arius.Api.Integration.Tests --filter ScenarioRegistryTests`
Expected: FAIL — `ScenarioRegistry` / `ArchiveScenario` do not exist.

- [ ] **Step 3: Implement**

`src/Arius.Api.Integration.Tests/Harness/ScenarioRegistry.cs`:

```csharp
using System.Collections.Concurrent;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.Cost;
using Mediator;

namespace Arius.Api.Integration.Tests.Harness;

/// <summary>A scripted archive run: publish <paramref name="Events"/> in order, then return <paramref name="Result"/>.</summary>
public sealed record ArchiveScenario(IReadOnlyList<INotification> Events, ArchiveResult Result);

/// <summary>
/// A scripted restore run: publish <paramref name="PreCostEvents"/>; if <paramref name="CostPrompt"/> is set,
/// invoke the run's ConfirmRehydration callback with it (declined/timed-out ⇒ stop, return <paramref name="Result"/>);
/// otherwise publish <paramref name="PostApproveEvents"/> and return <paramref name="Result"/>.
/// </summary>
public sealed record RestoreScenario(
    IReadOnlyList<INotification> PreCostEvents,
    RestoreCostEstimate? CostPrompt,
    IReadOnlyList<INotification> PostApproveEvents,
    RestoreResult Result);

/// <summary>Holds the next scripted scenario for each repository. Set by tests, taken by the scripted handlers.</summary>
public sealed class ScenarioRegistry
{
    private readonly ConcurrentDictionary<long, ArchiveScenario> _archive = new();
    private readonly ConcurrentDictionary<long, RestoreScenario> _restore = new();

    public void SetArchive(long repositoryId, ArchiveScenario scenario) => _archive[repositoryId] = scenario;
    public void SetRestore(long repositoryId, RestoreScenario scenario) => _restore[repositoryId] = scenario;

    public ArchiveScenario? TakeArchive(long repositoryId) => _archive.TryGetValue(repositoryId, out var s) ? s : null;
    public RestoreScenario? TakeRestore(long repositoryId) => _restore.TryGetValue(repositoryId, out var s) ? s : null;
}
```

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test src/Arius.Api.Integration.Tests --filter ScenarioRegistryTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Api.Integration.Tests/Harness/ScenarioRegistry.cs src/Arius.Api.Integration.Tests/ScenarioRegistryTests.cs
git commit -m "test(api): add ScenarioRegistry + archive/restore scenario types"
```

---

## Task 4: `ScriptedArchiveHandler`

**Files:**
- Create: `src/Arius.Api.Integration.Tests/Harness/ScriptedArchiveHandler.cs`
- Create: `src/Arius.Api.Integration.Tests/ScriptedArchiveHandlerTests.cs`

**Interfaces:**
- Produces: `ScriptedArchiveHandler : ICommandHandler<ArchiveCommand, ArchiveResult>` — ctor `(IPublisher publisher, ArchiveScenario scenario)`. Publishes `scenario.Events` in order, returns `scenario.Result`.
- Consumes: `IPublisher` (Mediator), `ArchiveScenario` (Task 3).

The scripted handler is resolved from the per-repo provider; the composer (Task 5) registers both it and the repo's `ArchiveScenario`.

- [ ] **Step 1: Write the failing test**

`src/Arius.Api.Integration.Tests/ScriptedArchiveHandlerTests.cs`:

```csharp
using Arius.Api.Integration.Tests.Harness;
using Arius.Core.Features.ArchiveCommand;
using Mediator;
using NSubstitute;

namespace Arius.Api.Integration.Tests;

public class ScriptedArchiveHandlerTests
{
    [Test]
    public async Task Publishes_events_in_order_then_returns_result()
    {
        var publisher = Substitute.For<IPublisher>();
        var scan = new ScanCompleteEvent(2, 3000);
        var uploaded = new ChunkUploadedEvent(ChunkHash.Parse(new string('c', 64)), 400, 2000);
        var result = NewArchiveResult();
        var handler = new ScriptedArchiveHandler(publisher, new ArchiveScenario([scan, uploaded], result));

        var actual = await handler.Handle(new ArchiveCommand(new ArchiveCommandOptions { RootDirectory = "/x" }), default);

        await Assert.That(actual).IsSameReferenceAs(result);
        Received.InOrder(() =>
        {
            publisher.Publish(scan, Arg.Any<CancellationToken>());
            publisher.Publish(uploaded, Arg.Any<CancellationToken>());
        });
    }

    private static ArchiveResult NewArchiveResult() => new()
    {
        Success = true, FilesScanned = 2, EntriesExcluded = 0, FilesUploaded = 1, FilesDeduped = 0,
        OriginalSize = 3000, IncrementalSize = 2000, IncrementalStoredSize = 400, FastHashReused = 0,
        FastHashRehashed = 2, RootHash = null, SnapshotTime = DateTimeOffset.UnixEpoch,
    };
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test src/Arius.Api.Integration.Tests --filter ScriptedArchiveHandlerTests`
Expected: FAIL — `ScriptedArchiveHandler` does not exist.

- [ ] **Step 3: Implement**

`src/Arius.Api.Integration.Tests/Harness/ScriptedArchiveHandler.cs`:

```csharp
using Arius.Core.Features.ArchiveCommand;
using Mediator;

namespace Arius.Api.Integration.Tests.Harness;

/// <summary>Fake archive handler: publishes the scenario's real Core events (so the Api's forwarders +
/// JobSink run exactly as in production), then returns the scenario's result. No storage is touched.</summary>
public sealed class ScriptedArchiveHandler(IPublisher publisher, ArchiveScenario scenario)
    : ICommandHandler<ArchiveCommand, ArchiveResult>
{
    public async ValueTask<ArchiveResult> Handle(ArchiveCommand command, CancellationToken cancellationToken)
    {
        foreach (var evt in scenario.Events)
            await publisher.Publish(evt, cancellationToken);
        return scenario.Result;
    }
}
```

> Mediator's `ICommandHandler<TCommand,TResult>.Handle` returns `ValueTask<TResult>`. If the generated interface differs, match its exact signature (see the real `ArchiveCommandHandler`).

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test src/Arius.Api.Integration.Tests --filter ScriptedArchiveHandlerTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Api.Integration.Tests/Harness/ScriptedArchiveHandler.cs src/Arius.Api.Integration.Tests/ScriptedArchiveHandlerTests.cs
git commit -m "test(api): add ScriptedArchiveHandler that publishes scripted archive events"
```

---

## Task 5: `ScriptedRepositoryCoreComposer` + wire it into the factory

**Files:**
- Create: `src/Arius.Api.Integration.Tests/Harness/ScriptedRepositoryCoreComposer.cs`
- Modify: `src/Arius.Api.Integration.Tests/Harness/AriusApiFactory.cs` (`ConfigureWebHost`)

**Interfaces:**
- Produces: `ScriptedRepositoryCoreComposer : IRepositoryCoreComposer` — for the repo in `connection`, registers `FakeStorageCostEstimator`, the repo's scripted `ArchiveScenario`/`RestoreScenario` (from `ScenarioRegistry`), and the scripted command handlers. `AriusApiFactory.Scenarios` now exposes the registered `ScenarioRegistry`.
- Consumes: `ScenarioRegistry` (Task 3), `ScriptedArchiveHandler` (Task 4), `ScriptedRestoreHandler` (Task 8 — registered conditionally when a restore scenario exists), `FakeStorageCostEstimator` (`Arius.Tests.Shared`).

- [ ] **Step 1: Implement the composer**

`src/Arius.Api.Integration.Tests/Harness/ScriptedRepositoryCoreComposer.cs`:

```csharp
using Arius.Api.AppData;
using Arius.Api.Composition;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.Cost;
using Arius.Core.Shared.Storage;
using Arius.Tests.Shared.Fakes;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Api.Integration.Tests.Harness;

/// <summary>Test composer: registers a scripted Core (scenario-driven command handlers + a deterministic
/// cost estimator) into the per-repo provider. Never opens a real container.</summary>
public sealed class ScriptedRepositoryCoreComposer(ScenarioRegistry scenarios) : IRepositoryCoreComposer
{
    public Task ComposeAsync(IServiceCollection services, RepositoryConnection connection, PreflightMode mode, CancellationToken cancellationToken)
    {
        services.AddSingleton<IStorageCostEstimator>(new FakeStorageCostEstimator());

        var archive = scenarios.TakeArchive(connection.RepositoryId);
        if (archive is not null)
        {
            services.AddSingleton(archive);
            services.AddSingleton<ICommandHandler<ArchiveCommand, ArchiveResult>, ScriptedArchiveHandler>();
        }

        var restore = scenarios.TakeRestore(connection.RepositoryId);
        if (restore is not null)
        {
            services.AddSingleton(restore);
            services.AddSingleton<ICommandHandler<RestoreCommand, RestoreResult>, ScriptedRestoreHandler>();
        }

        return Task.CompletedTask;
    }
}
```

> `FakeStorageCostEstimator`'s constructor: confirm with `grep -n "public FakeStorageCostEstimator" src/Arius.Tests.Shared/Fakes/FakeStorageCostEstimator.cs`. If it requires arguments (e.g. a region string), pass the documented defaults. `ScriptedRestoreHandler` is created in Task 8; until then, comment out the restore block or land Task 8 before running a restore scenario.

- [ ] **Step 2: Wire the composer + registry into the factory**

In `AriusApiFactory.cs`, replace the `ConfigureWebHost` body and expose `Scenarios`:

```csharp
    public ScenarioRegistry Scenarios { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Arius:AppDbPath", _dbPath);
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(Scenarios);
            services.RemoveAll<IRepositoryCoreComposer>();
            services.AddSingleton<IRepositoryCoreComposer, ScriptedRepositoryCoreComposer>();
        });
    }
```

Add `using Microsoft.Extensions.DependencyInjection.Extensions;` (for `RemoveAll`) to the factory.

- [ ] **Step 3: Build + re-run existing integration tests**

Run: `dotnet test src/Arius.Api.Integration.Tests`
Expected: `HealthSmokeTests` + Task 3/4 unit tests still PASS (health path never composes a repo provider, so the scripted composer is inert here).

- [ ] **Step 4: Commit**

```bash
git add src/Arius.Api.Integration.Tests/Harness/ScriptedRepositoryCoreComposer.cs src/Arius.Api.Integration.Tests/Harness/AriusApiFactory.cs
git commit -m "test(api): wire ScriptedRepositoryCoreComposer into the WebApplicationFactory"
```

---

## Task 6: End-to-end archive scenario via `JobRunner`

Proves the seam: scripted Core → real forwarders → `JobSink` → `AppDatabase`.

**Files:**
- Create: `src/Arius.Api.Integration.Tests/ArchiveScenarioTests.cs`

**Interfaces:**
- Consumes: `AriusApiFactory` (Tasks 2/5), `JobRunner` (from `factory.Services`), `AppDatabase.GetJob`.

- [ ] **Step 1: Write the failing test**

`src/Arius.Api.Integration.Tests/ArchiveScenarioTests.cs`:

```csharp
using Arius.Api.AppData;
using Arius.Api.Integration.Tests.Harness;
using Arius.Api.Jobs;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Hashes;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Api.Integration.Tests;

public class ArchiveScenarioTests
{
    [Test]
    public async Task Scripted_archive_runs_to_completion_and_records_the_job()
    {
        await using var factory = new AriusApiFactory();
        var srcDir = Path.Combine(Path.GetTempPath(), $"arius-itest-src-{Guid.NewGuid():N}");
        Directory.CreateDirectory(srcDir);
        var repoId = factory.SeedRepository(localPath: srcDir);

        factory.Scenarios.SetArchive(repoId, new ArchiveScenario(
            Events:
            [
                new ScanCompleteEvent(TotalFiles: 2, TotalBytes: 3000),
                new FileScannedEvent(RelativePath.Parse("a"), 2000),
                new FileHashingEvent(RelativePath.Parse("a"), 2000),
                new ChunkUploadedEvent(ChunkHash.Parse(new string('c', 64)), StoredSize: 400, OriginalSize: 2000),
                new FileDedupedEvent(ContentHash.Parse(new string('b', 64)), OriginalSize: 1000),
                new SnapshotCreatedEvent(default, DateTimeOffset.UnixEpoch, 2),
            ],
            Result: new ArchiveResult
            {
                Success = true, FilesScanned = 2, EntriesExcluded = 0, FilesUploaded = 1, FilesDeduped = 1,
                OriginalSize = 3000, IncrementalSize = 2000, IncrementalStoredSize = 400, FastHashReused = 0,
                FastHashRehashed = 2, RootHash = null, SnapshotTime = DateTimeOffset.UnixEpoch,
            }));

        var runner = factory.Services.GetRequiredService<JobRunner>();
        var jobId = Guid.NewGuid().ToString();
        await runner.RunArchiveAsync(repoId, jobId, tier: "Archive", removeLocal: false, writePointers: false, fastHash: false);

        var db = factory.Services.GetRequiredService<AppDatabase>();
        var job = db.GetJob(jobId);
        await Assert.That(job).IsNotNull();
        await Assert.That(job!.Status).IsEqualTo("completed");
    }
}
```

> `RunArchiveAsync` is `public` and awaitable; calling it directly (rather than the fire-and-forget hub path) lets the test await completion deterministically. `SnapshotCreatedEvent`'s first arg is a `FileTreeHash` — `default` is fine for the fake. If `RunArchiveAsync`'s signature differs, match `src/Arius.Api/Jobs/JobRunner.cs`.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test src/Arius.Api.Integration.Tests --filter ArchiveScenarioTests`
Expected: initially FAIL if the scripted handler isn't resolved (e.g. Mediator resolves the concrete real handler instead of the registered interface). If so, this is the moment to confirm Othamar Mediator dispatches via `GetRequiredService<ICommandHandler<…>>` — the whole harness depends on it. See the troubleshooting note below.

- [ ] **Step 3: Make it pass**

If Step 2 already passes (Mediator resolves by interface), no code change is needed — proceed to commit.

If it fails because the **real** `ArchiveCommandHandler` ran (no scripted registration took effect): Othamar Mediator resolves handlers from DI by the `ICommandHandler<TCommand,TResult>` service type, and the scripted composer registered exactly that — so a failure here means the real handler was *also* registered and won. Ensure the scripted composer does **not** call `AddArius` (it doesn't) and that `AddMediator()` in the registry did not auto-register a Core command handler (it only discovers handlers in the Api assembly; the Core handlers come from `AddArius`, which the scripted path skips). No production change should be required; if a duplicate registration exists, `services.RemoveAll<ICommandHandler<ArchiveCommand, ArchiveResult>>()` before adding the scripted one in the composer.

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test src/Arius.Api.Integration.Tests --filter ArchiveScenarioTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Api.Integration.Tests/ArchiveScenarioTests.cs
git commit -m "test(api): end-to-end scripted archive scenario drives JobRunner to completion"
```

---

## Task 7: Hub-driven archive over SignalR

Proves the realtime path (StartArchive → job group → progress/done → `AttachToJob`) that the review findings depend on.

**Files:**
- Create: `src/Arius.Api.Integration.Tests/ArchiveHubTests.cs`

**Interfaces:**
- Consumes: `AriusApiFactory`, the SignalR hub at `/hubs/arius`, `HubConnection` (client). Uses `factory.Server.CreateHandler()` so the client talks to the in-memory test server.

- [ ] **Step 1: Write the failing test**

`src/Arius.Api.Integration.Tests/ArchiveHubTests.cs`:

```csharp
using Arius.Api.Integration.Tests.Harness;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Hashes;
using Microsoft.AspNetCore.SignalR.Client;

namespace Arius.Api.Integration.Tests;

public class ArchiveHubTests
{
    [Test]
    public async Task StartArchive_over_the_hub_completes_and_attach_returns_terminal_state()
    {
        await using var factory = new AriusApiFactory();
        var srcDir = Path.Combine(Path.GetTempPath(), $"arius-itest-src-{Guid.NewGuid():N}");
        Directory.CreateDirectory(srcDir);
        var repoId = factory.SeedRepository(localPath: srcDir);

        factory.Scenarios.SetArchive(repoId, new ArchiveScenario(
            Events:
            [
                new ScanCompleteEvent(1, 2000),
                new FileScannedEvent(RelativePath.Parse("a"), 2000),
                new ChunkUploadedEvent(ChunkHash.Parse(new string('c', 64)), 400, 2000),
            ],
            Result: new ArchiveResult
            {
                Success = true, FilesScanned = 1, EntriesExcluded = 0, FilesUploaded = 1, FilesDeduped = 0,
                OriginalSize = 2000, IncrementalSize = 2000, IncrementalStoredSize = 400, FastHashReused = 0,
                FastHashRehashed = 1, RootHash = null, SnapshotTime = DateTimeOffset.UnixEpoch,
            }));

        var handler = factory.Server.CreateHandler();
        await using var conn = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/arius", o =>
            {
                o.HttpMessageHandlerFactory = _ => handler;
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
            })
            .Build();

        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.On<string, string, string?>("Done", (status, _, _2) => done.TrySetResult(status));

        await conn.StartAsync();
        var jobId = await conn.InvokeAsync<string>("StartArchive", repoId, "Archive", false, false, false);

        var terminal = await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(terminal).IsEqualTo("completed");
        await Assert.That(jobId).IsNotNull();
    }
}
```

> The `Done` message arity/param names come from `JobSink.Done` / the hub's client contract — confirm with `grep -n "\"Done\"\|SendAsync(\"Done" src/Arius.Api/Jobs/JobSink.cs src/Arius.Api/Hubs`. Adjust the `On<...>` generic args to match. `HttpMessageHandlerFactory` is the standard way to point the SignalR client at a `TestServer`.

- [ ] **Step 2: Run it to verify it fails, then passes**

Run: `dotnet test src/Arius.Api.Integration.Tests --filter ArchiveHubTests`
Expected: FAIL first (message contract mismatch is the usual cause); after aligning the `On<...>` signature to `JobSink.Done`, PASS.

- [ ] **Step 3: Commit**

```bash
git add src/Arius.Api.Integration.Tests/ArchiveHubTests.cs
git commit -m "test(api): hub-driven scripted archive completes over SignalR"
```

---

## Task 8: `ScriptedRestoreHandler` + cost-handshake integration test

**Files:**
- Create: `src/Arius.Api.Integration.Tests/Harness/ScriptedRestoreHandler.cs`
- Create: `src/Arius.Api.Integration.Tests/RestoreCostHandshakeTests.cs`

**Interfaces:**
- Produces: `ScriptedRestoreHandler : ICommandHandler<RestoreCommand, RestoreResult>` — ctor `(IPublisher publisher, RestoreScenario scenario)`. Publishes `PreCostEvents`; if `CostPrompt` set, invokes `command.Options.ConfirmRehydration(CostPrompt, ct)` (null result ⇒ return `Result` without post-approve events); else publishes `PostApproveEvents`; returns `Result`.
- Consumes: `RestoreScenario` (Task 3), `IPublisher`, `RestoreCommand`/`RestoreOptions.ConfirmRehydration` (real Core types).

- [ ] **Step 1: Implement the handler**

`src/Arius.Api.Integration.Tests/Harness/ScriptedRestoreHandler.cs`:

```csharp
using Arius.Core.Features.RestoreCommand;
using Mediator;

namespace Arius.Api.Integration.Tests.Harness;

/// <summary>Fake restore handler: publishes scripted restore events and drives the run's ConfirmRehydration
/// cost handshake exactly as the real handler would, so JobRunner's awaiting-cost path runs unchanged.</summary>
public sealed class ScriptedRestoreHandler(IPublisher publisher, RestoreScenario scenario)
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
                return scenario.Result;   // declined / timed-out — run stops (mirrors Core exiting with pending)
        }

        foreach (var evt in scenario.PostApproveEvents)
            await publisher.Publish(evt, cancellationToken);

        return scenario.Result;
    }
}
```

- [ ] **Step 2: Write the failing test (approve path)**

`src/Arius.Api.Integration.Tests/RestoreCostHandshakeTests.cs`:

```csharp
using Arius.Api.AppData;
using Arius.Api.Integration.Tests.Harness;
using Arius.Api.Jobs;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.Cost;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Api.Integration.Tests;

public class RestoreCostHandshakeTests
{
    [Test]
    public async Task Approving_the_cost_prompt_lets_the_restore_complete()
    {
        await using var factory = new AriusApiFactory();
        var dest = Path.Combine(Path.GetTempPath(), $"arius-itest-dst-{Guid.NewGuid():N}");
        var repoId = factory.SeedRepository(localPath: dest);

        factory.Scenarios.SetRestore(repoId, new RestoreScenario(
            PreCostEvents:
            [
                new SnapshotResolvedEvent(DateTimeOffset.UnixEpoch, default),
                new TreeTraversalCompleteEvent(FileCount: 3, TotalOriginalSize: 3000),
                new ChunkResolutionCompleteEvent(TotalChunks: 5, LargeCount: 1, TarCount: 1, TotalChunkBytes: 3000),
                new RehydrationStatusEvent(Available: 3, Rehydrated: 0, NeedsRehydration: 2, Pending: 0),
            ],
            CostPrompt: new RestoreCostEstimate
            {
                ChunksAvailable = 3, ChunksAlreadyRehydrated = 0, ChunksNeedingRehydration = 2, ChunksPendingRehydration = 0,
                BytesNeedingRehydration = 1200, BytesPendingRehydration = 0, DownloadBytes = 3000,
                TotalStandard = 0.71, TotalHigh = 4.31,
                StandardWait = TimeSpan.FromHours(15), HighWait = TimeSpan.FromHours(1),
            },
            PostApproveEvents:
            [
                new FileRestoredEvent(RelativePath.Parse("a"), 1000),
                new FileRestoredEvent(RelativePath.Parse("b"), 1000),
                new FileRestoredEvent(RelativePath.Parse("c"), 1000),
            ],
            Result: new RestoreResult
            {
                Success = true, FilesRestored = 3, FilesSkipped = 0, ChunksPendingRehydration = 0, ErrorMessage = null,
            }));

        var runner = factory.Services.GetRequiredService<JobRunner>();
        var approvals = factory.Services.GetRequiredService<RestoreApprovalRegistry>();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        var jobId = Guid.NewGuid().ToString();

        var run = runner.RunRestoreAsync(repoId, jobId, connectionId: "test", version: null,
            targetPaths: [], overwrite: false, noPointers: false);

        // Wait for the job to park at awaiting-cost, then approve High priority.
        await WaitUntil(() => db.GetJob(jobId)?.Status == "awaiting-cost", TimeSpan.FromSeconds(10));
        approvals.Resolve(jobId, RehydratePriority.High);

        await run;
        await Assert.That(db.GetJob(jobId)!.Status).IsEqualTo("completed");
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
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

> Confirm `RestoreApprovalRegistry.Resolve`'s signature and that approving feeds `answer.Approved == true` with `answer.Priority` (see `src/Arius.Api/Jobs/RestoreApprovalRegistry.cs`). `RehydratePriority` lives in `Arius.Core.Shared.Storage`. If `Resolve` takes `(jobId, RehydratePriority?)`, `null` = decline (used by later cancel tests).

- [ ] **Step 3: Uncomment the restore block in the composer**

If Task 5's restore registration was commented out pending this task, re-enable it now.

- [ ] **Step 4: Run it to verify it fails, then passes**

Run: `dotnet test src/Arius.Api.Integration.Tests --filter RestoreCostHandshakeTests`
Expected: PASS (job parks at awaiting-cost, approval resumes it to completed).

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Api.Integration.Tests/Harness/ScriptedRestoreHandler.cs src/Arius.Api.Integration.Tests/RestoreCostHandshakeTests.cs src/Arius.Api.Integration.Tests/Harness/ScriptedRepositoryCoreComposer.cs
git commit -m "test(api): scripted restore drives the cost-approval handshake to completion"
```

---

## Task 9: Canonical scenarios + fidelity guard (contract conformance)

Provides reusable representative scenarios (consumed by Plans 2 & 3) and a guard that the scripted archive/restore events obey Core's documented ordering preconditions, so the fakes cannot silently drift into event sequences the real pipeline never emits.

**Files:**
- Create: `src/Arius.Api.Integration.Tests/Harness/CanonicalScenarios.cs`
- Create: `src/Arius.Api.Integration.Tests/FidelityTests.cs`

**Interfaces:**
- Produces: `CanonicalScenarios.RepresentativeArchive()` → `ArchiveScenario`, `CanonicalScenarios.RehydratingRestore()` → `RestoreScenario`. Sized on the handoff's representative run (≈3,122 files / 3.16 GB, some dedup, some archive-tier chunks).

> **Deferred to Plan 2:** the stronger *real-Core event-capture diff* (run the real `ArchiveCommandHandler`/`RestoreCommandHandler` over `FakeInMemoryBlobContainerService` with a capturing `IPublisher` and assert type-and-order equality against these canonical scenarios). Plan 2 is already in the event internals and constructs the real handlers, so the capture harness is cheap to add there. Plan 1 ships the contract-conformance guard below.

- [ ] **Step 1: Implement canonical scenarios**

`src/Arius.Api.Integration.Tests/Harness/CanonicalScenarios.cs`:

```csharp
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.Cost;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Hashes;

namespace Arius.Api.Integration.Tests.Harness;

/// <summary>Reusable representative scenarios modelled on the handoff's reference run. Kept in one place so
/// Plans 2 &amp; 3 exercise the same shapes the fidelity guard validates.</summary>
public static class CanonicalScenarios
{
    public static ArchiveScenario RepresentativeArchive() => new(
        Events:
        [
            new ScanCompleteEvent(TotalFiles: 3122, TotalBytes: 3_160_000_000),
            new FileScannedEvent(RelativePath.Parse("big.bin"), 100_000_000),
            new FileHashingEvent(RelativePath.Parse("big.bin"), 100_000_000),
            new ChunkUploadedEvent(ChunkHash.Parse(new string('a', 64)), StoredSize: 60_000_000, OriginalSize: 100_000_000),
            new FileDedupedEvent(ContentHash.Parse(new string('b', 64)), OriginalSize: 48_000_000),
            new SnapshotCreatedEvent(default, DateTimeOffset.UnixEpoch, 3122),
        ],
        Result: new ArchiveResult
        {
            Success = true, FilesScanned = 3122, EntriesExcluded = 0, FilesUploaded = 1, FilesDeduped = 1,
            OriginalSize = 3_160_000_000, IncrementalSize = 100_000_000, IncrementalStoredSize = 60_000_000,
            FastHashReused = 0, FastHashRehashed = 3122, RootHash = null, SnapshotTime = DateTimeOffset.UnixEpoch,
        });

    public static RestoreScenario RehydratingRestore() => new(
        PreCostEvents:
        [
            new SnapshotResolvedEvent(DateTimeOffset.UnixEpoch, default),
            new TreeTraversalCompleteEvent(FileCount: 3122, TotalOriginalSize: 3_160_000_000),
            new ChunkResolutionCompleteEvent(TotalChunks: 427, LargeCount: 12, TarCount: 40, TotalChunkBytes: 2_760_000_000),
            new RehydrationStatusEvent(Available: 145, Rehydrated: 0, NeedsRehydration: 282, Pending: 0),
        ],
        CostPrompt: new RestoreCostEstimate
        {
            ChunksAvailable = 145, ChunksAlreadyRehydrated = 0, ChunksNeedingRehydration = 282, ChunksPendingRehydration = 0,
            BytesNeedingRehydration = 2_100_000_000, BytesPendingRehydration = 0, DownloadBytes = 2_760_000_000,
            TotalStandard = 0.71, TotalHigh = 4.31, StandardWait = TimeSpan.FromHours(15), HighWait = TimeSpan.FromHours(1),
        },
        PostApproveEvents:
        [
            new RehydrationStatusEvent(Available: 145, Rehydrated: 282, NeedsRehydration: 0, Pending: 0),
            new FileRestoredEvent(RelativePath.Parse("big.bin"), 100_000_000),
        ],
        Result: new RestoreResult { Success = true, FilesRestored = 3122, FilesSkipped = 0, ChunksPendingRehydration = 0, ErrorMessage = null });
}
```

- [ ] **Step 2: Write the conformance test**

`src/Arius.Api.Integration.Tests/FidelityTests.cs`:

```csharp
using Arius.Api.Integration.Tests.Harness;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.RestoreCommand;

namespace Arius.Api.Integration.Tests;

public class FidelityTests
{
    [Test]
    public async Task Canonical_archive_obeys_core_event_ordering()
    {
        var events = CanonicalScenarios.RepresentativeArchive().Events;

        // ScanComplete must precede any per-file/upload event (Core enumerates before it hashes/uploads).
        var scanIdx = IndexOf<ScanCompleteEvent>(events);
        var firstFileIdx = IndexOfAny(events, e => e is FileScannedEvent or FileHashingEvent or ChunkUploadedEvent);
        await Assert.That(scanIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(scanIdx).IsLessThan(firstFileIdx);
    }

    [Test]
    public async Task Canonical_restore_resolves_chunks_before_reporting_rehydration_and_only_prompts_when_archive_tier()
    {
        var scenario = CanonicalScenarios.RehydratingRestore();
        var pre = scenario.PreCostEvents;

        var resolveIdx = IndexOf<ChunkResolutionCompleteEvent>(pre);
        var rehydIdx = IndexOf<RehydrationStatusEvent>(pre);
        await Assert.That(resolveIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(resolveIdx).IsLessThan(rehydIdx);

        // A cost prompt is present iff the pre-cost classification reported archive-tier chunks.
        var status = (RehydrationStatusEvent)pre[rehydIdx];
        await Assert.That(scenario.CostPrompt is not null).IsEqualTo(status.NeedsRehydration > 0);
    }

    private static int IndexOf<T>(IReadOnlyList<Mediator.INotification> events)
        => IndexOfAny(events, e => e is T);

    private static int IndexOfAny(IReadOnlyList<Mediator.INotification> events, Func<Mediator.INotification, bool> pred)
    {
        for (var i = 0; i < events.Count; i++) if (pred(events[i])) return i;
        return -1;
    }
}
```

- [ ] **Step 3: Run it**

Run: `dotnet test src/Arius.Api.Integration.Tests --filter FidelityTests`
Expected: PASS.

- [ ] **Step 4: Full suite green**

Run: `dotnet test src/Arius.Api.Integration.Tests`
Expected: all Task 2-9 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Api.Integration.Tests/Harness/CanonicalScenarios.cs src/Arius.Api.Integration.Tests/FidelityTests.cs
git commit -m "test(api): canonical scenarios + contract-conformance fidelity guard"
```

---

## Self-review

**Spec coverage (design §2):** seam (Task 1) ✓; scripted fake handlers + cost handshake (Tasks 4, 8) ✓; `FakeStorageCostEstimator` reuse (Task 5) ✓; scenario selection in-process (Tasks 3, 5) ✓ [control endpoint deferred to Plan 3, noted]; new `Arius.Api.Integration.Tests` tier with `WebApplicationFactory` (Task 2) ✓; `public partial class Program` (Task 1) ✓; fidelity guard (Task 9) ✓ [real-Core diff deferred to Plan 2, noted]. Three test tiers: unit (Tasks 3, 4, 9) ✓, Api integration (Tasks 6, 7, 8) ✓, e2e → Plan 3.

**Placeholder scan:** no TBD/TODO. The `>` notes are verification/adaptation instructions (confirm an exact signature), not deferred work — each names the exact grep to run and what to match. Two scoped deferrals (control endpoint → Plan 3; real-Core diff → Plan 2) are explicit and justified.

**Type consistency:** `ScenarioRegistry` methods (`SetArchive/TakeArchive/SetRestore/TakeRestore`) match across Tasks 3/5/6/8; `ArchiveScenario(Events, Result)` and `RestoreScenario(PreCostEvents, CostPrompt, PostApproveEvents, Result)` used identically in Tasks 3, 4, 6, 8, 9; handler ctors `(IPublisher, ArchiveScenario)` / `(IPublisher, RestoreScenario)` match their registrations in Task 5; `IRepositoryCoreComposer.ComposeAsync` signature identical in Tasks 1 and 5; event/result field names match the real Core records read from `Events.cs` / `ArchiveCommand.cs` / `RestoreCommand.cs` / `IStorageCostEstimator.cs`.

**Assumption to verify early (Task 6):** Othamar `Mediator` dispatches commands via `IServiceProvider.GetRequiredService<ICommandHandler<TCommand,TResult>>` — the whole swap depends on it. Task 6 Step 2/3 is where it is proven; the fallback (`RemoveAll` before add) is documented.
