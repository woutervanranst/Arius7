# Api/Web Vertical Slices — Phase 1 (production code) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restructure `Arius.Api` (C#) and `Arius.Web` (Angular) to Arius.Core's `Features/<Slice>/` + `Shared/<Mechanic>/` grammar with zero behavior change, keeping the existing test suites green as the regression net.

**Architecture:** REPR endpoints inside resource slices, no mediator. The single SignalR hub (`JobsHub`, wire-frozen at `/hubs/arius`) lives in `Features/Jobs/` and delegates its three foreign methods to slice-owned services. `AppDatabase` stays whole in `Shared/AppData/`. Web: `api-models.ts`/`ApiService` split per server domain inside `core/api/`; only `snapshot.store` moves into `features/repo/`; ESLint boundary rules added. Spec: `docs/superpowers/specs/2026-07-11-api-web-vertical-slices-design.md`.

**Tech Stack:** .NET 10 minimal APIs + SignalR, TUnit + Shouldly, ArchUnitNET, Angular 21, Playwright.

## Global Constraints

- Branch `refactor/api-web-slices-phase1` off `master`; the `jobs-progress` branch must be merged into master first (it touches the Jobs machinery).
- Wire contract frozen: REST routes, verbs, DTO property shapes, SignalR hub route `/hubs/arius`, hub method names, message names (`Log`, `Progress`, `Done`, `CostEstimate`). SQLite schema frozen.
- **Type names never change** (tests reference them) — only namespaces and file locations change. The one exception is enumerated cleanup C2 below.
- Test-file edits allowed: `using`/namespace/import-path lines and duplicate-`using` removal ONLY, plus the exact edits of enumerated cleanups. Any other test edit = contract drift → STOP and report.
- Enumerated cleanups (complete list — nothing else is in scope):
  - **C1** `CreateSchedule` Created Location header `/api/repos/{id}/schedules/{sid}` → `/repos/{id}/schedules/{sid}` (siblings don't include `/api`; SPA never reads Location). Test impact: none.
  - **C2** `RepositoryEndpoints`'s internal region helpers move to a new `RepositoryRegionResolver` static class (forced by the REPR split). Test impact: `Arius.Api.Tests/RepositoryRegionResolutionTests.cs` — one `using` + 4 call sites + 1 doc-cref, `RepositoryEndpoints.` → `RepositoryRegionResolver.` (Task 5).
  - **C3** Normalize fully-qualified debris (`System.Text.Json.JsonSerializer`, `Arius.Api.Jobs.X` inline qualifiers) into `using` directives while files are rewritten. No wire impact.
  - **C4** `JobsHub` constructor drops `SecretProtector`, `IBlobServiceFactory`, `RepositoryProviderRegistry` once delegation lands (no longer used). Not wire-visible.
- New namespaces: `Arius.Api.Features.<Slice>` and `Arius.Api.Shared.<Area>` (file-scoped, folder = namespace). Root files keep `Arius.Api`.
- Gates after every task: `dotnet build src/Arius.slnx` (0 errors) and `dotnet test src/Arius.Api.Tests` + `dotnet test src/Arius.Api.Integration.Tests` (0 failed). Web tasks gate on `npm run build` + `npm test -- --watch=false` in `src/Arius.Web`. Hermetic e2e runs at Tasks 12 and 17.
- Commit after every task with `refactor(api):` / `refactor(web):` prefixes; every commit message ends with:
  `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`

**File-move rule:** always `git mv` (preserves history). Test projects glob sources; no `.csproj` edits are needed for moves.

---

### Task 1: Baseline route + hub inventory

**Files:**
- Create: `docs/superpowers/plans/artifacts/2026-07-11-phase1-baseline-routes.txt`
- Create: `docs/superpowers/plans/artifacts/2026-07-11-phase1-baseline-hub.txt`

**Interfaces:** Produces the parity baseline Task 12 diffs against.

- [ ] **Step 1: Capture the REST route table from the running dev host**

```bash
mkdir -p docs/superpowers/plans/artifacts
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5199 \
  dotnet run --project src/Arius.Api --no-launch-profile &
API_PID=$!
sleep 10
curl -s http://localhost:5199/openapi/v1.json | python3 -c "
import json,sys
d=json.load(sys.stdin)
for p,ms in sorted(d['paths'].items()):
    for m in sorted(ms): print(m.upper(), p)
" > docs/superpowers/plans/artifacts/2026-07-11-phase1-baseline-routes.txt
kill $API_PID
cat docs/superpowers/plans/artifacts/2026-07-11-phase1-baseline-routes.txt
```

Expected: one line per route (GET /api/accounts, POST /api/accounts, … ~20 lines). If the OpenAPI document omits any endpoint, append it manually from `AriusApiHost.cs` + `Endpoints/*.cs`.

- [ ] **Step 2: Capture the hub method inventory**

```bash
grep -E 'public (async )?(Task|IAsyncEnumerable)' src/Arius.Api/Hubs/JobsHub.cs \
  | sed -E 's/^\s+//' > docs/superpowers/plans/artifacts/2026-07-11-phase1-baseline-hub.txt
cat docs/superpowers/plans/artifacts/2026-07-11-phase1-baseline-hub.txt
```

Expected: 12 signatures (StreamContainers, StartArchive, StartRestore, AttachToJob, DetachFromJob, CancelJob, SearchAll, ApproveRestore, DeclineRestore, SetAutoResume, ResumeRestore, StreamEntries).

- [ ] **Step 3: Commit**

```bash
git add docs/superpowers/plans/artifacts/
git commit -m "chore(api): capture phase-1 wire-contract baseline"
```

---

### Task 2: Move Shared mechanics (`AppData`, `Composition`, `Extensions`)

**Files:**
- Move: `src/Arius.Api/AppData/{AppDatabase,Records,JobStatuses,SecretProtector}.cs` → `src/Arius.Api/Shared/AppData/`
- Move: `src/Arius.Api/Composition/{RepositoryProviderRegistry,IRepositoryCoreComposer,AzureRepositoryCoreComposer}.cs` → `src/Arius.Api/Shared/Composition/`
- Move: `src/Arius.Api/Jobs/PeriodicTimerExtensions.cs` → `src/Arius.Api/Shared/Extensions/`
- Modify: every file with `using Arius.Api.AppData` / `using Arius.Api.Composition` (production, `Arius.Api.Testing`, `Arius.Api.Tests`, `Arius.Api.Integration.Tests`)

**Interfaces:**
- Produces: namespaces `Arius.Api.Shared.AppData`, `Arius.Api.Shared.Composition`, `Arius.Api.Shared.Extensions`. All type names unchanged. Every later task depends on these namespaces.

- [ ] **Step 1: Move files**

```bash
cd src/Arius.Api
mkdir -p Shared/AppData Shared/Composition Shared/Extensions
git mv AppData/AppDatabase.cs AppData/Records.cs AppData/JobStatuses.cs AppData/SecretProtector.cs Shared/AppData/
git mv Composition/RepositoryProviderRegistry.cs Composition/IRepositoryCoreComposer.cs Composition/AzureRepositoryCoreComposer.cs Shared/Composition/
git mv Jobs/PeriodicTimerExtensions.cs Shared/Extensions/
rmdir AppData Composition
cd ../..
```

- [ ] **Step 2: Rewrite namespaces repo-wide** (declarations AND usings in one pass — the strings are identical)

```bash
LC_ALL=C find src -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' -exec sed -i '' \
  -e 's/Arius\.Api\.AppData/Arius.Api.Shared.AppData/g' \
  -e 's/Arius\.Api\.Composition/Arius.Api.Shared.Composition/g' {} +
```

- [ ] **Step 3: Fix `PeriodicTimerExtensions` namespace and its consumers**

In `src/Arius.Api/Shared/Extensions/PeriodicTimerExtensions.cs` change `namespace Arius.Api.Jobs;` → `namespace Arius.Api.Shared.Extensions;`.

`SafeWaitForNextTickAsync` was namespace-local to `Arius.Api.Jobs`; find every consumer and add the using:

```bash
grep -rln 'SafeWaitForNextTickAsync' src/Arius.Api --include='*.cs' | grep -v Shared/Extensions
```

For each hit (expected: `Jobs/SchedulerService.cs`, `Jobs/RehydrationPollingService.cs`, `Jobs/StaleApprovalSweepService.cs` — verify with the grep) add `using Arius.Api.Shared.Extensions;`.

- [ ] **Step 4: Fix relative-qualified references the sed cannot catch**

```bash
grep -rn 'AppData\.\|Composition\.' src/Arius.Api --include='*.cs' | grep -v 'Arius.Api.Shared' | grep -v obj
```

Known hit: `src/Arius.Api/Jobs/SchedulerService.cs` uses `services.GetRequiredService<AppData.AppDatabase>()`. Change to `services.GetRequiredService<AppDatabase>()` and add `using Arius.Api.Shared.AppData;`. Fix any other hits the same way.

- [ ] **Step 5: Build and test**

```bash
dotnet build src/Arius.slnx
dotnet test src/Arius.Api.Tests
dotnet test src/Arius.Api.Integration.Tests
```

Expected: build succeeds; all tests pass. Test files changed only in `using` lines (verify: `git diff --stat src/Arius.Api.Tests src/Arius.Api.Integration.Tests src/Arius.Api.Testing`).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor(api): move AppData/Composition/Extensions under Shared/"
```

---

### Task 3: Filesystem slice

**Files:**
- Create: `src/Arius.Api/Features/Filesystem/ListFilesystem.cs`
- Create: `src/Arius.Api/Features/Filesystem/FilesystemSlice.cs`
- Delete: `src/Arius.Api/Endpoints/FilesystemEndpoints.cs`
- Modify: `src/Arius.Api/AriusApiHost.cs`, `src/Arius.Api/Contracts/Dtos.cs` (remove `FsEntryDto`/`FsListDto`)

**Interfaces:**
- Produces: `FilesystemSlice.MapFilesystem(this IEndpointRouteBuilder api)`; records `FsEntryDto`, `FsListDto` in `Arius.Api.Features.Filesystem` (names unchanged).

- [ ] **Step 1: Create `Features/Filesystem/ListFilesystem.cs`**

```csharp
namespace Arius.Api.Features.Filesystem;

/// <summary>A directory as the Arius.Api host/container sees it.</summary>
public sealed record FsEntryDto(string Name, string Path);

/// <summary>A directory listing: the resolved path, its parent (null at the root), and immediate subdirectories.</summary>
public sealed record FsListDto(string Path, string? Parent, IReadOnlyList<FsEntryDto> Entries);

/// <summary>
/// GET /api/fs/list — server-side directory browsing for the local-path picker. Lists directories <b>as the
/// Arius.Api host/container sees them</b> — under Docker these are the mounted volumes; in dev they are real
/// folders on the machine running the API. The stored local path must resolve here (not on the browser's
/// machine), which is why the picker is server-driven.
/// </summary>
internal static class ListFilesystemEndpoint
{
    public static void Map(IEndpointRouteBuilder api) => api.MapGet("/fs/list", Handle);

    private static IResult Handle(string? path)
    {
        // Default to the filesystem root the API can see; the client navigates from there.
        var target = string.IsNullOrWhiteSpace(path) ? DefaultRoot() : path.Trim();

        DirectoryInfo directory;
        try
        {
            directory = new DirectoryInfo(target);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            return Results.BadRequest($"Invalid path: {ex.Message}");
        }

        if (!directory.Exists)
            return Results.NotFound($"Directory not found: {target}");

        List<FsEntryDto> entries;
        try
        {
            entries = directory.EnumerateDirectories()
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Select(d => new FsEntryDto(d.Name, d.FullName))
                .ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return Results.Problem($"Cannot read directory: {ex.Message}", statusCode: StatusCodes.Status403Forbidden);
        }

        return Results.Ok(new FsListDto(directory.FullName, directory.Parent?.FullName, entries));
    }

    private static string DefaultRoot()
        => Path.GetPathRoot(Environment.CurrentDirectory) is { Length: > 0 } root ? root : "/";
}
```

- [ ] **Step 2: Create `Features/Filesystem/FilesystemSlice.cs`**

```csharp
namespace Arius.Api.Features.Filesystem;

/// <summary>Registration + route mapping for the Filesystem slice.</summary>
internal static class FilesystemSlice
{
    public static void MapFilesystem(this IEndpointRouteBuilder api) => ListFilesystemEndpoint.Map(api);
}
```

- [ ] **Step 3: Rewire and delete the old file**

- In `AriusApiHost.cs`: add `using Arius.Api.Features.Filesystem;`, replace `api.MapFilesystemEndpoints();` with `api.MapFilesystem();`.
- In `Contracts/Dtos.cs`: delete the `FsEntryDto` and `FsListDto` records and the `── Filesystem browse` section comment.
- `git rm src/Arius.Api/Endpoints/FilesystemEndpoints.cs`

- [ ] **Step 4: Build, test, commit**

```bash
dotnet build src/Arius.slnx && dotnet test src/Arius.Api.Tests && dotnet test src/Arius.Api.Integration.Tests
git add -A && git commit -m "refactor(api): extract Filesystem slice (REPR)"
```

---

### Task 4: Accounts slice (+ hub `StreamContainers` delegation)

**Files:**
- Create: `src/Arius.Api/Features/Accounts/Models.cs`, `ListAccounts.cs`, `GetAccount.cs`, `CreateAccount.cs`, `UpdateAccount.cs`, `DeleteAccount.cs`, `ContainerNameService.cs`, `AccountsSlice.cs`
- Delete: `src/Arius.Api/Endpoints/AccountEndpoints.cs`
- Modify: `src/Arius.Api/Hubs/JobsHub.cs`, `src/Arius.Api/AriusApiHost.cs`, `src/Arius.Api/Contracts/Dtos.cs` (remove account DTOs)

**Interfaces:**
- Consumes: `Arius.Api.Shared.AppData` (`AppDatabase`, `AccountRecord`, `SecretProtector`), `Arius.Api.Shared.Composition` (`RepositoryProviderRegistry`).
- Produces: `AccountsSlice.AddAccounts(this IServiceCollection)` / `MapAccounts(this IEndpointRouteBuilder)`; `public sealed class ContainerNameService` with `IAsyncEnumerable<string> StreamAsync(long accountId, string? accountName, string? accountKey, CancellationToken cancellationToken)`. Record names `AccountDto`, `CreateAccountRequest`, `UpdateAccountRequest` unchanged.

- [ ] **Step 1: Create `Features/Accounts/Models.cs`**

```csharp
using Arius.Api.Shared.AppData;

namespace Arius.Api.Features.Accounts;

/// <summary>A storage account as shown to the client. The account key is never returned.</summary>
public sealed record AccountDto(long Id, string Name, int Repositories, bool HasKey);

internal static class AccountMapping
{
    public static AccountDto ToDto(AppDatabase db, AccountRecord account)
        => new(account.Id, account.Name, db.CountRepositoriesForAccount(account.Id), account.EncryptedAccountKey is not null);
}
```

- [ ] **Step 2: Create the five REPR endpoint files**

`Features/Accounts/ListAccounts.cs`:

```csharp
using Arius.Api.Shared.AppData;

namespace Arius.Api.Features.Accounts;

/// <summary>GET /api/accounts — all storage accounts.</summary>
internal static class ListAccountsEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapGet("/", Handle);

    private static List<AccountDto> Handle(AppDatabase db)
        => db.ListAccounts().Select(a => AccountMapping.ToDto(db, a)).ToList();
}
```

`Features/Accounts/GetAccount.cs`:

```csharp
using Arius.Api.Shared.AppData;

namespace Arius.Api.Features.Accounts;

/// <summary>GET /api/accounts/{id} — one storage account.</summary>
internal static class GetAccountEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapGet("/{id:long}", Handle);

    private static IResult Handle(long id, AppDatabase db)
    {
        var account = db.GetAccount(id);
        return account is null ? Results.NotFound() : Results.Ok(AccountMapping.ToDto(db, account));
    }
}
```

`Features/Accounts/CreateAccount.cs`:

```csharp
using Arius.Api.Shared.AppData;

namespace Arius.Api.Features.Accounts;

public sealed record CreateAccountRequest(string Name, string? AccountKey);

/// <summary>POST /api/accounts — creates a storage account (key stored encrypted).</summary>
internal static class CreateAccountEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapPost("/", Handle);

    private static IResult Handle(CreateAccountRequest request, AppDatabase db, SecretProtector secrets)
    {
        var id = db.InsertAccount(request.Name, secrets.Protect(request.AccountKey));
        var account = db.GetAccount(id)!;
        return Results.Created($"/accounts/{id}", AccountMapping.ToDto(db, account));
    }
}
```

`Features/Accounts/UpdateAccount.cs`:

```csharp
using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;

namespace Arius.Api.Features.Accounts;

/// <summary>Account-flyout update. A <c>null</c> <see cref="AccountKey"/> leaves the stored key unchanged.</summary>
public sealed record UpdateAccountRequest(string? AccountKey);

/// <summary>PATCH /api/accounts/{id} — account-flyout edit: rotate the key. A null key in the request leaves the
/// stored key unchanged; rotating it invalidates cached providers so the new key takes effect on rebuild.</summary>
internal static class UpdateAccountEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapPatch("/{id:long}", Handle);

    private static IResult Handle(long id, UpdateAccountRequest request, AppDatabase db, SecretProtector secrets, RepositoryProviderRegistry registry)
    {
        if (db.GetAccount(id) is null)
            return Results.NotFound();

        var keyChanged = request.AccountKey is not null;

        db.UpdateAccount(id, secrets.Protect(request.AccountKey));

        if (keyChanged)
            foreach (var repoId in db.ListRepositoryIdsForAccount(id))
                registry.Evict(repoId);

        return Results.Ok(AccountMapping.ToDto(db, db.GetAccount(id)!));
    }
}
```

`Features/Accounts/DeleteAccount.cs`:

```csharp
using Arius.Api.Shared.AppData;

namespace Arius.Api.Features.Accounts;

/// <summary>DELETE /api/accounts/{id} — refused while the account still has repositories.</summary>
internal static class DeleteAccountEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapDelete("/{id:long}", Handle);

    private static IResult Handle(long id, AppDatabase db)
    {
        if (db.GetAccount(id) is null)
            return Results.NotFound();
        if (db.CountRepositoriesForAccount(id) > 0)
            return Results.Conflict("Account still has repositories.");

        db.DeleteAccount(id);
        return Results.NoContent();
    }
}
```

- [ ] **Step 3: Create `Features/Accounts/ContainerNameService.cs`** (body lifted verbatim from `JobsHub.StreamContainers`)

```csharp
using System.Runtime.CompilerServices;
using Arius.Api.Shared.AppData;
using Arius.Core.Shared.Storage;

namespace Arius.Api.Features.Accounts;

/// <summary>
/// Streams the container names in an account (Add-existing wizard). Pass <c>accountId</c> &gt; 0 to use a
/// configured account's stored key, or 0 with an explicit name + key for a new account.
/// Public only because it is injected into the public <c>JobsHub</c> (single-hub delivery seam, adr-0022).
/// </summary>
public sealed class ContainerNameService(AppDatabase database, SecretProtector secrets, IBlobServiceFactory blobServiceFactory)
{
    public async IAsyncEnumerable<string> StreamAsync(
        long accountId,
        string? accountName,
        string? accountKey,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string name;
        string? key;
        if (accountId > 0)
        {
            var account = database.GetAccount(accountId);
            if (account is null) yield break;
            name = account.Name;
            key = secrets.Unprotect(account.EncryptedAccountKey);
        }
        else
        {
            name = accountName ?? string.Empty;
            key = accountKey;
        }

        var blobService = await blobServiceFactory.CreateAsync(name, key, cancellationToken).ConfigureAwait(false);
        await foreach (var container in blobService.GetContainerNamesAsync(cancellationToken).ConfigureAwait(false))
            yield return container;
    }
}
```

- [ ] **Step 4: Create `Features/Accounts/AccountsSlice.cs`**

```csharp
namespace Arius.Api.Features.Accounts;

/// <summary>Registration + route mapping for the Accounts slice.</summary>
internal static class AccountsSlice
{
    public static IServiceCollection AddAccounts(this IServiceCollection services)
        => services.AddSingleton<ContainerNameService>();

    public static void MapAccounts(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/accounts");
        ListAccountsEndpoint.Map(group);
        GetAccountEndpoint.Map(group);
        CreateAccountEndpoint.Map(group);
        UpdateAccountEndpoint.Map(group);
        DeleteAccountEndpoint.Map(group);
    }
}
```

- [ ] **Step 5: Slim the hub method (cleanup C4, first part)**

In `src/Arius.Api/Hubs/JobsHub.cs`:
- Add `using Arius.Api.Features.Accounts;`.
- Constructor: remove parameters `SecretProtector secrets` and `IBlobServiceFactory blobServiceFactory`; add `ContainerNameService containerNames`. (Keep `using Arius.Core.Shared.Storage;` — `RehydratePriority` still needs it.)
- Replace the whole `StreamContainers` method (keep its `<summary>`) with:

```csharp
    public IAsyncEnumerable<string> StreamContainers(
        long accountId,
        string? accountName,
        string? accountKey,
        CancellationToken cancellationToken)
        => containerNames.StreamAsync(accountId, accountName, accountKey, cancellationToken);
```

- Remove the now-unused `using System.Runtime.CompilerServices;` ONLY if `StreamEntries`/`SearchAll` no longer need it (they do until Tasks 6/8 — leave it).

- [ ] **Step 6: Rewire host, prune old files**

- `AriusApiHost.cs`: add `using Arius.Api.Features.Accounts;`; replace `api.MapAccountEndpoints();` with `api.MapAccounts();`; add `builder.Services.AddAccounts();` after the `RepositoryProviderRegistry` registration.
- `Contracts/Dtos.cs`: delete `AccountDto`, `CreateAccountRequest`, `UpdateAccountRequest` and the `── Accounts` section comment.
- `git rm src/Arius.Api/Endpoints/AccountEndpoints.cs`

- [ ] **Step 7: Build, test, commit**

```bash
dotnet build src/Arius.slnx && dotnet test src/Arius.Api.Tests && dotnet test src/Arius.Api.Integration.Tests
git add -A && git commit -m "refactor(api): extract Accounts slice (REPR) + hub StreamContainers delegation"
```

---

### Task 5: Repositories slice (cleanup C2)

**Files:**
- Create: `src/Arius.Api/Features/Repositories/Models.cs`, `RepositoryRegionResolver.cs`, `ListRepositories.cs`, `GetRepository.cs`, `CreateRepository.cs`, `UpdateRepository.cs`, `DeleteRepository.cs`, `RepositoriesSlice.cs`
- Delete: `src/Arius.Api/Endpoints/RepositoryEndpoints.cs`
- Modify: `src/Arius.Api/AriusApiHost.cs`, `src/Arius.Api/Contracts/Dtos.cs`, `src/Arius.Api.Tests/RepositoryRegionResolutionTests.cs` (declared edit C2)

**Interfaces:**
- Produces: `RepositoriesSlice.MapRepositories(this IEndpointRouteBuilder)`; `internal static class RepositoryRegionResolver` with `internal static Task<RepositoryDto> ResolveRegionDtoAsync(AppDatabase db, RepositoryRecord repository, Func<long, CancellationToken, Task<StorageAccountInfo?>> resolveLive, CancellationToken ct)` and `internal static Task<StorageAccountInfo?> TryGetAccountInfoAsync(RepositoryProviderRegistry registry, long repositoryId, CancellationToken ct)`; `internal static class RepositoryMapping` with `ToDto(AppDatabase, RepositoryRecord, string? region, bool regionIsDefault)` and `NormalizeTier(string?)`. Record names `RepositoryDto`, `CreateRepositoryRequest`, `UpdateRepositoryRequest` unchanged.

- [ ] **Step 1: Create `Features/Repositories/Models.cs`**

```csharp
using Arius.Api.Shared.AppData;

namespace Arius.Api.Features.Repositories;

/// <summary>A repository as shown to the client. Secrets (key, passphrase) are never returned.</summary>
public sealed record RepositoryDto(
    long    Id,
    string  Alias,
    string  Container,
    long    AccountId,
    string  Account,
    string? LocalPath,
    string  DefaultTier,
    string? Region,
    bool    RegionIsDefault);

public sealed record CreateRepositoryRequest(
    long    AccountId,
    string  Container,
    string  Alias,
    string? Passphrase,
    string? LocalPath,
    string? DefaultTier);

/// <summary>Properties-screen update. Null fields are left unchanged (so secrets need not be resupplied).</summary>
public sealed record UpdateRepositoryRequest(
    string? Alias,
    string? LocalPath,
    string? DefaultTier,
    string? Passphrase);

internal static class RepositoryMapping
{
    public static RepositoryDto ToDto(AppDatabase db, RepositoryRecord repository, string? region, bool regionIsDefault)
    {
        var accountName = db.GetAccount(repository.AccountId)?.Name ?? "";
        return new RepositoryDto(
            repository.Id, repository.Alias, repository.Container, repository.AccountId, accountName,
            repository.LocalPath, repository.DefaultTier,
            Region:          region,
            RegionIsDefault: regionIsDefault);
    }

    public static string NormalizeTier(string? tier)
        => string.IsNullOrWhiteSpace(tier) ? "archive" : tier.Trim().ToLowerInvariant();
}
```

- [ ] **Step 2: Create `Features/Repositories/RepositoryRegionResolver.cs`** (bodies verbatim from `RepositoryEndpoints`, `ToDto` → `RepositoryMapping.ToDto`; `TryGetAccountInfoAsync` becomes `internal` so both list/get endpoints share it)

```csharp
using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;
using Arius.Core.Features.StorageAccountInfoQuery;
using Mediator;

namespace Arius.Api.Features.Repositories;

/// <summary>Region resolution for repository DTOs through a DB-backed read-through cache.</summary>
internal static class RepositoryRegionResolver
{
    /// <summary>Builds a repository DTO, resolving its region through a DB-backed read-through cache.</summary>
    internal static async Task<RepositoryDto> ResolveRegionDtoAsync(
        AppDatabase db,
        RepositoryRecord repository,
        Func<long, CancellationToken, Task<StorageAccountInfo?>> resolveLive,
        CancellationToken ct)
    {
        if (repository.RegionHint is not null)
            return RepositoryMapping.ToDto(db, repository, repository.RegionHint, regionIsDefault: false);

        var info = await resolveLive(repository.Id, ct);
        if (info is { RegionIsDefault: false })
            db.SetRepositoryRegionHint(repository.Id, info.Region); // cache only a configured region (immutable); leave unset ones to re-resolve
        return RepositoryMapping.ToDto(db, repository, info?.Region, info?.RegionIsDefault ?? false);
    }

    /// <summary>
    /// Resolves a repository's storage-account info (currently the pricing region) through Core's Mediator.
    /// Best-effort: a Mediator query needs the repo's read provider, and an unreachable or misconfigured
    /// container throws while that provider is built — degrade to <c>null</c> (rendered as unknown) rather
    /// than failing the caller.
    /// </summary>
    internal static async Task<StorageAccountInfo?> TryGetAccountInfoAsync(RepositoryProviderRegistry registry, long repositoryId, CancellationToken ct)
    {
        try
        {
            var provider = await registry.GetReadProviderAsync(repositoryId, ct);
            return await provider.GetRequiredService<IMediator>().Send(new StorageAccountInfoQuery(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
```

- [ ] **Step 3: Create the five REPR endpoint files** (bodies verbatim from `RepositoryEndpoints`, helpers renamed)

`Features/Repositories/ListRepositories.cs`:

```csharp
using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;

namespace Arius.Api.Features.Repositories;

/// <summary>GET /api/repos — all repositories, regions resolved via the read-through cache.</summary>
internal static class ListRepositoriesEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapGet("/", Handle);

    private static async Task<List<RepositoryDto>> Handle(AppDatabase db, RepositoryProviderRegistry registry, CancellationToken ct)
    {
        var repositories = db.ListRepositories();
        var dtos = await Task.WhenAll(repositories.Select(r =>
            RepositoryRegionResolver.ResolveRegionDtoAsync(db, r, (id, c) => RepositoryRegionResolver.TryGetAccountInfoAsync(registry, id, c), ct)));
        return dtos.ToList();
    }
}
```

`Features/Repositories/GetRepository.cs`:

```csharp
using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;

namespace Arius.Api.Features.Repositories;

/// <summary>GET /api/repos/{id} — one repository, region resolved via the read-through cache.</summary>
internal static class GetRepositoryEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapGet("/{id:long}", Handle);

    private static async Task<IResult> Handle(long id, AppDatabase db, RepositoryProviderRegistry registry, CancellationToken ct)
    {
        var repository = db.GetRepository(id);
        if (repository is null)
            return Results.NotFound();
        return Results.Ok(await RepositoryRegionResolver.ResolveRegionDtoAsync(db, repository, (rid, c) => RepositoryRegionResolver.TryGetAccountInfoAsync(registry, rid, c), ct));
    }
}
```

`Features/Repositories/CreateRepository.cs`:

```csharp
using Arius.Api.Shared.AppData;

namespace Arius.Api.Features.Repositories;

/// <summary>POST /api/repos — registers a repository under an existing account.</summary>
internal static class CreateRepositoryEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapPost("/", Handle);

    private static IResult Handle(CreateRepositoryRequest request, AppDatabase db, SecretProtector secrets)
    {
        if (db.GetAccount(request.AccountId) is null)
            return Results.BadRequest($"Account {request.AccountId} does not exist.");

        var id = db.InsertRepository(
            request.Alias,
            request.Container,
            request.AccountId,
            request.LocalPath,
            RepositoryMapping.NormalizeTier(request.DefaultTier),
            secrets.Protect(request.Passphrase));

        // Region is left unresolved here (the container may not exist yet); the client refetches the list, which resolves and caches it.
        return Results.Created($"/repos/{id}", RepositoryMapping.ToDto(db, db.GetRepository(id)!, region: null, regionIsDefault: false));
    }
}
```

`Features/Repositories/UpdateRepository.cs`:

```csharp
using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;

namespace Arius.Api.Features.Repositories;

/// <summary>PATCH /api/repos/{id} — properties-screen update; null fields are left unchanged.</summary>
internal static class UpdateRepositoryEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapPatch("/{id:long}", Handle);

    private static IResult Handle(long id, UpdateRepositoryRequest request, AppDatabase db, SecretProtector secrets, RepositoryProviderRegistry registry)
    {
        if (db.GetRepository(id) is null)
            return Results.NotFound();

        db.UpdateRepository(
            id,
            request.Alias,
            request.LocalPath,
            request.DefaultTier is null ? null : RepositoryMapping.NormalizeTier(request.DefaultTier),
            secrets.Protect(request.Passphrase));

        // Connection material may have changed — drop the cached read provider so it rebuilds, discard any
        // memoized statistics, and invalidate the region cache so it re-resolves against the (possibly new) target.
        registry.Evict(id);
        db.ClearStatisticsCache(id);
        db.SetRepositoryRegionHint(id, null);
        // Region is left unresolved here; the client refetches the list, which re-resolves and caches it.
        return Results.Ok(RepositoryMapping.ToDto(db, db.GetRepository(id)!, region: null, regionIsDefault: false));
    }
}
```

`Features/Repositories/DeleteRepository.cs`:

```csharp
using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;

namespace Arius.Api.Features.Repositories;

/// <summary>DELETE /api/repos/{id} — removes the registry row; the Azure container is left intact.</summary>
internal static class DeleteRepositoryEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapDelete("/{id:long}", Handle);

    private static IResult Handle(long id, AppDatabase db, RepositoryProviderRegistry registry)
    {
        if (db.GetRepository(id) is null)
            return Results.NotFound();

        db.DeleteRepository(id);
        registry.Remove(id); // repo is gone for good → also dispose its rolling-log factory, not just Evict the provider
        return Results.NoContent();
    }
}
```

- [ ] **Step 4: Create `Features/Repositories/RepositoriesSlice.cs`**

```csharp
namespace Arius.Api.Features.Repositories;

/// <summary>Route mapping for the Repositories slice.</summary>
internal static class RepositoriesSlice
{
    public static void MapRepositories(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/repos");
        ListRepositoriesEndpoint.Map(group);
        GetRepositoryEndpoint.Map(group);
        CreateRepositoryEndpoint.Map(group);
        UpdateRepositoryEndpoint.Map(group);
        DeleteRepositoryEndpoint.Map(group);
    }
}
```

- [ ] **Step 5: Declared test edit (C2)** — in `src/Arius.Api.Tests/RepositoryRegionResolutionTests.cs`:

```bash
sed -i '' -e 's/using Arius\.Api\.Endpoints;/using Arius.Api.Features.Repositories;/' \
          -e 's/RepositoryEndpoints\./RepositoryRegionResolver./g' \
  src/Arius.Api.Tests/RepositoryRegionResolutionTests.cs
grep -n 'RepositoryEndpoints' src/Arius.Api.Tests/RepositoryRegionResolutionTests.cs
```

Expected: no remaining hits (the doc-comment `<see cref>` is covered by the second sed expression). No assertion changed.

- [ ] **Step 6: Rewire host, prune old files**

- `AriusApiHost.cs`: add `using Arius.Api.Features.Repositories;`; replace `api.MapRepositoryEndpoints();` with `api.MapRepositories();`.
- `Contracts/Dtos.cs`: delete `RepositoryDto`, `CreateRepositoryRequest`, `UpdateRepositoryRequest` and the `── Repositories` section comment.
- `git rm src/Arius.Api/Endpoints/RepositoryEndpoints.cs`

- [ ] **Step 7: Build, test, commit**

```bash
dotnet build src/Arius.slnx && dotnet test src/Arius.Api.Tests && dotnet test src/Arius.Api.Integration.Tests
git add -A && git commit -m "refactor(api): extract Repositories slice (REPR); region helpers -> RepositoryRegionResolver"
```

---

### Task 6: `Shared/Entries` + Browse slice (+ hub `StreamEntries` delegation)

**Files:**
- Create: `src/Arius.Api/Shared/Entries/EntryDto.cs` (moved types), `src/Arius.Api/Features/Browse/ListSnapshots.cs`, `EntryStreamer.cs`, `BrowseSlice.cs`
- Modify: `src/Arius.Api/Contracts/EntryDto.cs` (shrinks to `SearchHitDto` only), `src/Arius.Api/Endpoints/BrowseEndpoints.cs` (loses snapshots route), `src/Arius.Api/Hubs/JobsHub.cs`, `src/Arius.Api/AriusApiHost.cs`, `src/Arius.Api/Contracts/Dtos.cs` (remove `SnapshotDto`)

**Interfaces:**
- Produces: namespace `Arius.Api.Shared.Entries` holding `EntryDto`, `StateFlagsDto`, `EntryMapping` (names unchanged); `public sealed class EntryStreamer` with `IAsyncEnumerable<EntryDto> StreamAsync(long repositoryId, string? version, string? prefix, string? filter, bool includeLocal, CancellationToken cancellationToken)`; `BrowseSlice.AddBrowse/MapBrowse`; `SnapshotDto` in `Arius.Api.Features.Browse`.

- [ ] **Step 1: Create `Shared/Entries/EntryDto.cs`** — move `EntryDto`, `SearchHitDto` excluded, `StateFlagsDto`, `EntryMapping` verbatim from `Contracts/EntryDto.cs`, with `namespace Arius.Api.Shared.Entries;` (keep `using Arius.Core.Features.ListQuery;`). `EntryMapping` stays `internal static`.

- [ ] **Step 2: Shrink `Contracts/EntryDto.cs`** to only:

```csharp
using Arius.Api.Shared.Entries;

namespace Arius.Api.Contracts;

/// <summary>A cross-repository search hit: the entry plus its owning repository.</summary>
public sealed record SearchHitDto(long RepoId, string Repo, EntryDto Entry);
```

(The file moves fully into the Search slice in Task 8.)

- [ ] **Step 3: Create `Features/Browse/ListSnapshots.cs`**

```csharp
using Arius.Api.Shared.Composition;
using Arius.Core.Features.SnapshotsListQuery;
using Mediator;

namespace Arius.Api.Features.Browse;

public sealed record SnapshotDto(string Version, DateTimeOffset Timestamp, long FileCount);

/// <summary>GET /api/repos/{id}/snapshots — the repository's snapshots (time-travel).</summary>
internal static class ListSnapshotsEndpoint
{
    public static void Map(IEndpointRouteBuilder api) => api.MapGet("/repos/{id:long}/snapshots", Handle);

    private static async Task<List<SnapshotDto>> Handle(long id, RepositoryProviderRegistry registry, CancellationToken ct)
    {
        var provider = await registry.GetReadProviderAsync(id, ct);
        var mediator = provider.GetRequiredService<IMediator>();
        var snapshots = new List<SnapshotDto>();
        await foreach (var s in mediator.CreateStream(new SnapshotsListQuery(), ct))
            snapshots.Add(new SnapshotDto(s.Version, s.Timestamp, s.FileCount));
        return snapshots;
    }
}
```

- [ ] **Step 4: Create `Features/Browse/EntryStreamer.cs`** (body verbatim from `JobsHub.StreamEntries`)

```csharp
using System.Runtime.CompilerServices;
using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;
using Arius.Api.Shared.Entries;
using Arius.Core.Features.ListQuery;
using Arius.Core.Shared.FileSystem;
using Mediator;

namespace Arius.Api.Features.Browse;

/// <summary>
/// Streams the immediate children (directories + files) of a folder in a snapshot.
/// Public only because it is injected into the public <c>JobsHub</c> (single-hub delivery seam, adr-0022).
/// </summary>
public sealed class EntryStreamer(AppDatabase database, RepositoryProviderRegistry registry)
{
    /// <param name="version">Snapshot version (null/empty = latest).</param>
    /// <param name="prefix">Folder path within the repository (null/empty = root).</param>
    /// <param name="filter">Case-insensitive filename substring filter.</param>
    /// <param name="includeLocal">Overlay the repository's local folder onto the listing.</param>
    public async IAsyncEnumerable<EntryDto> StreamAsync(
        long repositoryId,
        string? version,
        string? prefix,
        string? filter,
        bool includeLocal,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var repository = database.GetRepository(repositoryId);
        var provider   = await registry.GetReadProviderAsync(repositoryId, cancellationToken);
        var mediator   = provider.GetRequiredService<IMediator>();

        var options = new ListQueryOptions
        {
            Version   = string.IsNullOrWhiteSpace(version) ? null : version,
            Prefix    = string.IsNullOrWhiteSpace(prefix) ? null : RelativePath.Parse(prefix),
            Filter    = string.IsNullOrWhiteSpace(filter) ? null : filter,
            Recursive = false,
            LocalPath = includeLocal ? repository?.LocalPath : null,
        };

        await foreach (var entry in mediator.CreateStream(new ListQuery(options), cancellationToken))
            yield return EntryMapping.ToDto(entry);
    }
}
```

- [ ] **Step 5: Create `Features/Browse/BrowseSlice.cs`**

```csharp
namespace Arius.Api.Features.Browse;

/// <summary>Registration + route mapping for the Browse slice.</summary>
internal static class BrowseSlice
{
    public static IServiceCollection AddBrowse(this IServiceCollection services)
        => services.AddSingleton<EntryStreamer>();

    public static void MapBrowse(this IEndpointRouteBuilder api) => ListSnapshotsEndpoint.Map(api);
}
```

- [ ] **Step 6: Slim the hub, shrink old files**

- `JobsHub.cs`: add `using Arius.Api.Features.Browse;` and `using Arius.Api.Shared.Entries;`; constructor: add `EntryStreamer entries`; replace the whole `StreamEntries` method (keep its `<summary>`/`<param>` docs) with:

```csharp
    public IAsyncEnumerable<EntryDto> StreamEntries(
        long repositoryId,
        string? version,
        string? prefix,
        string? filter,
        bool includeLocal,
        CancellationToken cancellationToken)
        => entries.StreamAsync(repositoryId, version, prefix, filter, includeLocal, cancellationToken);
```

  (`SearchAll` still uses `EntryMapping`/`ListQuery` — leave those usings until Task 8.)
- `Endpoints/BrowseEndpoints.cs`: delete the snapshots `MapGet` block and the now-unused `using Arius.Core.Features.SnapshotsListQuery;`. The stats route stays until Task 7.
- `Contracts/Dtos.cs`: delete `SnapshotDto` (keep `StatisticsDto`/`TierStatisticsDto` for Task 7).
- `AriusApiHost.cs`: add `using Arius.Api.Features.Browse;`, add `api.MapBrowse();` (before `api.MapBrowseEndpoints();`, which now only serves stats), add `builder.Services.AddBrowse();`.

- [ ] **Step 7: Build, test, commit**

```bash
dotnet build src/Arius.slnx && dotnet test src/Arius.Api.Tests && dotnet test src/Arius.Api.Integration.Tests
git add -A && git commit -m "refactor(api): extract Browse slice + Shared/Entries; hub StreamEntries delegation"
```

---

### Task 7: Statistics slice

**Files:**
- Create: `src/Arius.Api/Features/Statistics/GetStatistics.cs`, `StatisticsSlice.cs`
- Delete: `src/Arius.Api/Endpoints/BrowseEndpoints.cs`
- Modify: `src/Arius.Api/AriusApiHost.cs`, `src/Arius.Api/Contracts/Dtos.cs` (remove stats DTOs)

**Interfaces:**
- Produces: `StatisticsSlice.MapStatistics(this IEndpointRouteBuilder)`; `StatisticsDto`, `TierStatisticsDto` in `Arius.Api.Features.Statistics` (names unchanged).

- [ ] **Step 1: Create `Features/Statistics/GetStatistics.cs`** (endpoint body verbatim from `BrowseEndpoints`, including the memoization comment block)

```csharp
using System.Text.Json;
using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;
using Arius.Core.Features.StatisticsQuery;
using Arius.Core.Shared.Snapshot;
using Mediator;

namespace Arius.Api.Features.Statistics;

public sealed record StatisticsDto(
    long Files,
    long OriginalSize,
    long DeduplicatedSize,
    long StoredSize,
    long UniqueChunks,
    double TotalStorageCostPerMonth,
    IReadOnlyList<TierStatisticsDto> StoredByTier);

/// <summary>Stored size, distinct-chunk count, and estimated monthly storage cost for one storage tier (Hot/Cool/Cold/Archive).</summary>
public sealed record TierStatisticsDto(string Tier, long UniqueChunks, long StoredSize, double CostPerMonth);

/// <summary>
/// GET /api/repos/{id}/stats. `full=true` loads the whole chunk index so the repository-wide storage figures
/// are complete (slower); the web Statistics screen lazy-loads its storage section with that flag.
///
/// The result is memoized in the app database (see statistics_cache). A repository's statistics are a pure
/// function of its snapshot set, so a cache HIT is served straight from the local database with NO
/// blob-storage access — that is what makes a warm load fast. The cache is invalidated explicitly when the
/// snapshot set can have changed: after an archive (JobRunner) and on a properties change / delete
/// (Repositories slice). Only on a MISS do we touch storage: list the snapshot blobs once to stamp the
/// entry's fingerprint (the latest snapshot version, for provenance + pruning prior generations) and run the
/// real computation.
/// </summary>
internal static class GetStatisticsEndpoint
{
    public static void Map(IEndpointRouteBuilder api) => api.MapGet("/repos/{id:long}/stats", Handle);

    private static async Task<StatisticsDto> Handle(long id, string? version, bool? full, AppDatabase database, RepositoryProviderRegistry registry, CancellationToken ct)
    {
        var fullFlag   = full ?? false;
        var versionKey = version ?? string.Empty;

        var cached = database.GetCachedStatistics(id, versionKey, fullFlag);
        if (cached is not null)
            return JsonSerializer.Deserialize<StatisticsDto>(cached)!;

        var provider = await registry.GetReadProviderAsync(id, ct);
        var mediator = provider.GetRequiredService<IMediator>();

        // Miss: derive the fingerprint cheaply (latest blob name only — not SnapshotsListQuery, which
        // resolves every manifest), compute, and store.
        var snapshotService = provider.GetRequiredService<ISnapshotService>();
        var snapshotBlobs   = await snapshotService.ListBlobNamesAsync(ct);
        var fingerprint     = snapshotBlobs.Count == 0 ? string.Empty : snapshotService.GetVersion(snapshotBlobs[^1]);

        var statistics = await mediator.Send(new StatisticsQuery(version, fullFlag), ct);
        var dto = new StatisticsDto(
            statistics.Files, statistics.OriginalSize, statistics.DeduplicatedSize, statistics.StoredSize, statistics.UniqueChunks,
            statistics.TotalStorageCostPerMonth,
            statistics.StoredByTier.Select(t => new TierStatisticsDto(t.Tier.ToString(), t.UniqueChunks, t.StoredSize, t.CostPerMonth)).ToList());

        database.UpsertCachedStatistics(id, versionKey, fullFlag, fingerprint, JsonSerializer.Serialize(dto));
        return dto;
    }
}
```

- [ ] **Step 2: Create `Features/Statistics/StatisticsSlice.cs`**

```csharp
namespace Arius.Api.Features.Statistics;

/// <summary>Route mapping for the Statistics slice.</summary>
internal static class StatisticsSlice
{
    public static void MapStatistics(this IEndpointRouteBuilder api) => GetStatisticsEndpoint.Map(api);
}
```

- [ ] **Step 3: Rewire and delete**

- `AriusApiHost.cs`: add `using Arius.Api.Features.Statistics;`; replace `api.MapBrowseEndpoints();` with `api.MapStatistics();`; remove `using Arius.Api.Endpoints;` if `MapJobEndpoints` is the only remaining Endpoints call — it is not yet (Jobs/Schedules still there), so leave the using.
- `Contracts/Dtos.cs`: delete `StatisticsDto`, `TierStatisticsDto` and the `── Snapshots / stats` section comment.
- `git rm src/Arius.Api/Endpoints/BrowseEndpoints.cs`

- [ ] **Step 4: Build, test, commit**

```bash
dotnet build src/Arius.slnx && dotnet test src/Arius.Api.Tests && dotnet test src/Arius.Api.Integration.Tests
git add -A && git commit -m "refactor(api): extract Statistics slice (REPR)"
```

---

### Task 8: Search slice (+ hub `SearchAll` delegation)

**Files:**
- Create: `src/Arius.Api/Features/Search/Models.cs`, `RepositorySearcher.cs`, `SearchSlice.cs`
- Delete: `src/Arius.Api/Contracts/EntryDto.cs`
- Modify: `src/Arius.Api/Hubs/JobsHub.cs`, `src/Arius.Api/AriusApiHost.cs`

**Interfaces:**
- Produces: `SearchHitDto` in `Arius.Api.Features.Search` (name unchanged); `public sealed class RepositorySearcher` with `IAsyncEnumerable<SearchHitDto> SearchAsync(string query, CancellationToken cancellationToken)`; `SearchSlice.AddSearch(this IServiceCollection)`.

- [ ] **Step 1: Create `Features/Search/Models.cs`**

```csharp
using Arius.Api.Shared.Entries;

namespace Arius.Api.Features.Search;

/// <summary>A cross-repository search hit: the entry plus its owning repository.</summary>
public sealed record SearchHitDto(long RepoId, string Repo, EntryDto Entry);
```

- [ ] **Step 2: Create `Features/Search/RepositorySearcher.cs`** (body verbatim from `JobsHub.SearchAll`)

```csharp
using System.Runtime.CompilerServices;
using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;
using Arius.Api.Shared.Entries;
using Arius.Core.Features.ListQuery;
using Mediator;

namespace Arius.Api.Features.Search;

/// <summary>
/// Streams cross-repository search hits: runs a recursive filename filter across every repository
/// (each failure isolated so one unreachable repo doesn't fail the whole search).
/// Public only because it is injected into the public <c>JobsHub</c> (single-hub delivery seam, adr-0022).
/// </summary>
public sealed class RepositorySearcher(AppDatabase database, RepositoryProviderRegistry registry)
{
    public async IAsyncEnumerable<SearchHitDto> SearchAsync(string query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query)) yield break;

        foreach (var repo in database.ListRepositories())
        {
            var hits = new List<SearchHitDto>();
            try
            {
                var provider = await registry.GetReadProviderAsync(repo.Id, cancellationToken);
                var mediator = provider.GetRequiredService<IMediator>();
                await foreach (var entry in mediator.CreateStream(new ListQuery(new ListQueryOptions { Filter = query, Recursive = true }), cancellationToken))
                    if (entry is RepositoryFileEntry file)
                        hits.Add(new SearchHitDto(repo.Id, repo.Alias, EntryMapping.ToDto(file)));
            }
            catch
            {
                // Skip repositories that can't be opened or listed; keep searching the rest.
            }

            foreach (var hit in hits)
                yield return hit;
        }
    }
}
```

- [ ] **Step 3: Create `Features/Search/SearchSlice.cs`**

```csharp
namespace Arius.Api.Features.Search;

/// <summary>Registration for the Search slice (no REST routes — search streams over the hub).</summary>
internal static class SearchSlice
{
    public static IServiceCollection AddSearch(this IServiceCollection services)
        => services.AddSingleton<RepositorySearcher>();
}
```

- [ ] **Step 4: Slim the hub, delete the Contracts remnant**

- `JobsHub.cs`: add `using Arius.Api.Features.Search;`; constructor: add `RepositorySearcher searcher`; replace the whole `SearchAll` method (keep its `<summary>`) with:

```csharp
    public IAsyncEnumerable<SearchHitDto> SearchAll(string query, CancellationToken cancellationToken)
        => searcher.SearchAsync(query, cancellationToken);
```

  Now remove from `JobsHub.cs`: `using System.Runtime.CompilerServices;`, `using Arius.Core.Features.ListQuery;`, `using Arius.Core.Shared.FileSystem;`, `using Arius.Api.Shared.Entries;` (no longer referenced — verify each with a grep before removing).
- `git rm src/Arius.Api/Contracts/EntryDto.cs`
- `AriusApiHost.cs`: add `using Arius.Api.Features.Search;` and `builder.Services.AddSearch();`.

- [ ] **Step 5: Build, test, commit**

```bash
dotnet build src/Arius.slnx && dotnet test src/Arius.Api.Tests && dotnet test src/Arius.Api.Integration.Tests
git add -A && git commit -m "refactor(api): extract Search slice; hub SearchAll delegation"
```

---

### Task 9: Schedules slice (`IJobDispatcher` seam, cleanup C1)

**Files:**
- Create: `src/Arius.Api/Shared/Composition/IJobDispatcher.cs`, `src/Arius.Api/Features/Schedules/Models.cs`, `ListSchedules.cs`, `CreateSchedule.cs`, `DeleteSchedule.cs`, `SchedulesSlice.cs`
- Move: `src/Arius.Api/Jobs/SchedulerService.cs` → `src/Arius.Api/Features/Schedules/SchedulerService.cs`
- Modify: `src/Arius.Api/Jobs/JobRunner.cs` (implements `IJobDispatcher`), `src/Arius.Api/Endpoints/JobEndpoints.cs` (loses schedule routes), `src/Arius.Api/AriusApiHost.cs`, `src/Arius.Api/Contracts/Dtos.cs` (remove `ScheduleDto`/`CreateScheduleRequest`)

**Interfaces:**
- Produces: `public interface IJobDispatcher { Task RunArchiveAsync(long repositoryId, string jobId, string tier, bool removeLocal, bool writePointers, bool fastHash = false, string trigger = "one-off"); }` in `Arius.Api.Shared.Composition` — the ONE sanctioned Schedules→Jobs seam. `SchedulesSlice.AddSchedules/MapSchedules`. `ScheduleDto`, `CreateScheduleRequest` in `Arius.Api.Features.Schedules` (names unchanged).
- Consumes: `JobRunner.RunArchiveAsync` (signature above, already exists — `Jobs/JobRunner.cs:35`).

- [ ] **Step 1: Create `Shared/Composition/IJobDispatcher.cs`**

```csharp
namespace Arius.Api.Shared.Composition;

/// <summary>
/// The Schedules→Jobs seam: lets the cron scheduler enqueue archive jobs without referencing the Jobs
/// slice's internals (slice isolation, adr-0022). Implemented by <c>JobRunner</c>.
/// </summary>
public interface IJobDispatcher
{
    /// <summary>Runs an archive job end-to-end (fire-and-forget by callers; the Task completes when the job does).</summary>
    Task RunArchiveAsync(long repositoryId, string jobId, string tier, bool removeLocal, bool writePointers, bool fastHash = false, string trigger = "one-off");
}
```

- [ ] **Step 2: Implement it on `JobRunner`** — in `src/Arius.Api/Jobs/JobRunner.cs` change the class declaration `public sealed class JobRunner(` … `)` to add the base list `: IJobDispatcher` (after the primary-constructor parameter list's closing parenthesis), and add `using Arius.Api.Shared.Composition;` if not already present. `RunArchiveAsync`'s existing signature already matches.

- [ ] **Step 3: Create `Features/Schedules/Models.cs`**

```csharp
using Arius.Api.Shared.AppData;

namespace Arius.Api.Features.Schedules;

public sealed record ScheduleDto(long Id, long RepoId, string Cron, string Kind, bool Enabled, DateTimeOffset? NextRun);

public sealed record CreateScheduleRequest(string Cron, string? Kind);

internal static class ScheduleMapping
{
    public static ScheduleDto ToDto(ScheduleRecord s) => new(s.Id, s.RepositoryId, s.Cron, s.Kind, s.Enabled, s.NextRun);
}
```

- [ ] **Step 4: Create the three REPR endpoint files**

`Features/Schedules/ListSchedules.cs`:

```csharp
using Arius.Api.Shared.AppData;

namespace Arius.Api.Features.Schedules;

/// <summary>GET /api/repos/{id}/schedules — the repository's cron schedules.</summary>
internal static class ListSchedulesEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/repos/{id:long}/schedules", (long id, AppDatabase db) =>
            db.ListSchedules(id).Select(ScheduleMapping.ToDto).ToList());
}
```

`Features/Schedules/CreateSchedule.cs` (note cleanup **C1**: Location header loses the `/api` prefix):

```csharp
using Arius.Api.Shared.AppData;

namespace Arius.Api.Features.Schedules;

/// <summary>POST /api/repos/{id}/schedules — adds a cron schedule for the repository.</summary>
internal static class CreateScheduleEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/repos/{id:long}/schedules", (long id, CreateScheduleRequest request, AppDatabase db) =>
        {
            if (db.GetRepository(id) is null) return Results.NotFound();
            var scheduleId = db.InsertSchedule(id, request.Cron, request.Kind ?? "archive", enabled: true);
            return Results.Created($"/repos/{id}/schedules/{scheduleId}", ScheduleMapping.ToDto(db.ListSchedules(id).First(s => s.Id == scheduleId)));
        });
}
```

`Features/Schedules/DeleteSchedule.cs`:

```csharp
using Arius.Api.Shared.AppData;

namespace Arius.Api.Features.Schedules;

/// <summary>DELETE /api/repos/{id}/schedules/{scheduleId}.</summary>
internal static class DeleteScheduleEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapDelete("/repos/{id:long}/schedules/{scheduleId:long}", (long id, long scheduleId, AppDatabase db) =>
        {
            db.DeleteSchedule(scheduleId);
            return Results.NoContent();
        });
}
```

- [ ] **Step 5: Move + rewire `SchedulerService`**

```bash
git mv src/Arius.Api/Jobs/SchedulerService.cs src/Arius.Api/Features/Schedules/SchedulerService.cs
```

Then edit it: `namespace Arius.Api.Jobs;` → `namespace Arius.Api.Features.Schedules;`; add `using Arius.Api.Shared.Composition;` (`using Arius.Api.Shared.AppData;` and `using Arius.Api.Shared.Extensions;` were added in Task 2); in `Tick()` replace `services.GetRequiredService<JobRunner>()` with `services.GetRequiredService<IJobDispatcher>()` and rename the local `runner` accordingly. The `runner.RunArchiveAsync(...)` call compiles unchanged against the interface.

- [ ] **Step 6: Create `Features/Schedules/SchedulesSlice.cs`**

```csharp
namespace Arius.Api.Features.Schedules;

/// <summary>Registration + route mapping for the Schedules slice.</summary>
internal static class SchedulesSlice
{
    public static IServiceCollection AddSchedules(this IServiceCollection services)
    {
        services.AddHostedService<SchedulerService>();
        return services;
    }

    public static void MapSchedules(this IEndpointRouteBuilder api)
    {
        ListSchedulesEndpoint.Map(api);
        CreateScheduleEndpoint.Map(api);
        DeleteScheduleEndpoint.Map(api);
    }
}
```

- [ ] **Step 7: Rewire host and shrink `JobEndpoints`**

- `Endpoints/JobEndpoints.cs`: delete the three schedule routes and the private `ToDto` helper; the file keeps only `/jobs`, `/jobs/{id}`, `/jobs/{id}/warnings` (removed fully in Task 10).
- `AriusApiHost.cs`: add `using Arius.Api.Features.Schedules;`; add `api.MapSchedules();` after `api.MapJobEndpoints();`; replace `builder.Services.AddHostedService<Arius.Api.Jobs.SchedulerService>();` with `builder.Services.AddSchedules();`.
- `Contracts/Dtos.cs`: delete `ScheduleDto` and `CreateScheduleRequest` (leave `JobDto` for Task 10).

- [ ] **Step 8: Build, test, commit**

```bash
dotnet build src/Arius.slnx && dotnet test src/Arius.Api.Tests && dotnet test src/Arius.Api.Integration.Tests
git add -A && git commit -m "refactor(api): extract Schedules slice; IJobDispatcher seam; C1 Location cleanup"
```

---

### Task 10: Jobs slice (the big move) + final `AriusApiHost`

**Files:**
- Move (with `git mv`, namespace → `Arius.Api.Features.Jobs`): `Jobs/{JobRunner,JobSink,JobStateRegistry,JobViewResolver,JobFormat,JobSnapshot,PersistedJobState,RestoreApprovalRegistry,StaleApprovalSweepService,RehydrationPollingService,RehydrationSchedule}.cs` and `Hubs/{JobsHub,ArchiveForwarders,RestoreForwarders}.cs` → `src/Arius.Api/Features/Jobs/`
- Create: `src/Arius.Api/Features/Jobs/Models.cs`, `ListJobs.cs`, `GetJob.cs`, `GetJobWarnings.cs`, `JobsSlice.cs`
- Delete: `src/Arius.Api/Endpoints/JobEndpoints.cs`, `src/Arius.Api/Contracts/Dtos.cs`, `src/Arius.Api/Contracts/JobDetailDtos.cs` (folders `Endpoints/`, `Hubs/`, `Jobs/`, `Contracts/` disappear)
- Modify: `src/Arius.Api/AriusApiHost.cs` (final form), test/testing projects (using-line seds)

**Interfaces:**
- Produces: everything job-related in `Arius.Api.Features.Jobs` (type names unchanged: `JobsHub`, `JobRunner`, `JobSink`, `JobStateRegistry`, `JobViewResolver`, `JobSnapshot`, `JobOutcome`, `PersistedJobState`, `RestoreResumeState`, `RestoreApprovalRegistry`, `RehydrationSchedule`, `CostEstimateDto`, `ResumeInfo`, `JobAttachState`, `JobDetailDto`, `JobWarningsDto`, `JobDto`); `JobsSlice.AddJobs/MapJobs`.

- [ ] **Step 1: Move the machinery**

```bash
cd src/Arius.Api
mkdir -p Features/Jobs
git mv Jobs/JobRunner.cs Jobs/JobSink.cs Jobs/JobStateRegistry.cs Jobs/JobViewResolver.cs \
       Jobs/JobFormat.cs Jobs/JobSnapshot.cs Jobs/PersistedJobState.cs Jobs/RestoreApprovalRegistry.cs \
       Jobs/StaleApprovalSweepService.cs Jobs/RehydrationPollingService.cs Jobs/RehydrationSchedule.cs \
       Features/Jobs/
git mv Hubs/JobsHub.cs Hubs/ArchiveForwarders.cs Hubs/RestoreForwarders.cs Features/Jobs/
rmdir Jobs Hubs
cd ../..
```

- [ ] **Step 2: Rewrite the namespaces repo-wide** (`Arius.Api.Jobs`, `Arius.Api.Hubs`, and the test-visible `Arius.Api.Contracts` all become `Arius.Api.Features.Jobs`)

```bash
LC_ALL=C find src -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' -exec sed -i '' \
  -e 's/Arius\.Api\.Jobs/Arius.Api.Features.Jobs/g' \
  -e 's/Arius\.Api\.Hubs/Arius.Api.Features.Jobs/g' \
  -e 's/Arius\.Api\.Contracts/Arius.Api.Features.Jobs/g' {} +
```

- [ ] **Step 3: De-duplicate the merged usings** — files that imported two of the old namespaces now have identical duplicate `using Arius.Api.Features.Jobs;` lines (CS0105). Fix by deleting the duplicate line in each:

```bash
grep -rn 'using Arius.Api.Features.Jobs;' src --include='*.cs' | grep -v obj | awk -F: '{print $1}' | sort | uniq -d
```

Known duplicates (verify against the grep): `src/Arius.Api/Features/Jobs/JobRunner.cs`, `JobSink.cs`, `JobsHub.cs`, `JobViewResolver.cs`, `src/Arius.Api.Tests/Jobs/JobSinkAggregateTests.cs`, `Jobs/RepresentationTests.cs`, `src/Arius.Api.Integration.Tests/JobViewResolverTests.cs`, `LifecycleScenarioTests.cs`, `ReattachScenarioTests.cs`. Also remove self-namespace usings in the moved production files (a file in `namespace Arius.Api.Features.Jobs` needs no `using Arius.Api.Features.Jobs;`).

- [ ] **Step 4: Create `Features/Jobs/Models.cs`** (job DTOs from `Contracts/Dtos.cs` + `Contracts/JobDetailDtos.cs`, cleanup C3 applied: proper usings, no inline qualifiers)

```csharp
namespace Arius.Api.Features.Jobs;

public sealed record JobDto(
    string          Id,
    long            RepoId,
    string          Repo,
    string          Kind,
    string          Trigger,
    string          Status,
    double          Pct,
    string?         Detail,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string?         Outcome = null);

/// <summary>The cost-modal payload pushed on the <c>CostEstimate</c> message and returned by snapshot-on-attach
/// for an <c>awaiting-cost</c> job. jobId-tagged so a client attached to several jobs routes it.
/// Wait windows are the provider SLAs surfaced via <c>RestoreCostEstimate</c> — the modal renders
/// "up to {standardWaitHours} h".</summary>
public sealed record CostEstimateDto(
    string JobId,
    int    ChunksAvailable,
    int    ChunksNeedingRehydration,
    long   BytesNeedingRehydration,
    long   DownloadBytes,
    double TotalStandard,
    double TotalHigh,
    double StandardWaitHours,
    double HighWaitHours);

/// <summary>The parked-restore resume facts a reattaching client needs: whether auto-resume is on, and the
/// rehydration SLA window ("≈ hydrated by" = RehydrationStartedAt + RehydrationWindowHours). Null for jobs
/// with no restore-resume state.</summary>
public sealed record ResumeInfo(bool AutoResume, DateTimeOffset RehydrationStartedAt, double RehydrationWindowHours)
{
    /// <summary>Maps the persisted restore-resume state to the wire DTO (null-safe). Shared by JobsHub + the jobs endpoints.</summary>
    public static ResumeInfo? From(RestoreResumeState? r) =>
        r is null ? null : new ResumeInfo(r.AutoResume, r.RehydrationStartedAt, r.RehydrationWindow.TotalHours);
}

/// <summary>Snapshot-on-attach payload: the job's current status, its absolute progress snapshot,
/// the cost modal if it is awaiting-cost, and the live warning count. One round trip, one client apply-path.</summary>
public sealed record JobAttachState(string Status, JobSnapshot Snapshot, CostEstimateDto? Cost, int WarningCount, ResumeInfo? Resume);

/// <summary>Full single-job payload for GET /jobs/{id}: the history row plus the parsed progress snapshot
/// (live if running, else from state_json) and the warning count.</summary>
public sealed record JobDetailDto(
    string Id, long RepoId, string Repo, string Kind, string Trigger, string Status,
    double Pct, string? Detail, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    string? Outcome, JobSnapshot? Snapshot, int WarningCount, CostEstimateDto? Cost, ResumeInfo? Resume);

/// <summary>Verbatim per-job warnings for GET /jobs/{id}/warnings. <see cref="Truncated"/> is true when more than
/// the retained tail (200) were emitted, so <see cref="Count"/> &gt; <see cref="Lines"/>.Count.</summary>
public sealed record JobWarningsDto(int Count, IReadOnlyList<string> Lines, bool Truncated);
```

Then `git rm src/Arius.Api/Contracts/Dtos.cs src/Arius.Api/Contracts/JobDetailDtos.cs` (both must be empty of surviving types by now — `Dtos.cs` holds only `JobDto` at this point, which Models.cs above re-homes; verify nothing else remains before deleting).

- [ ] **Step 5: Create the three REPR endpoint files** (bodies verbatim from `JobEndpoints.cs`)

`Features/Jobs/ListJobs.cs`:

```csharp
using Arius.Api.Shared.AppData;

namespace Arius.Api.Features.Jobs;

/// <summary>GET /api/jobs — job history, optionally filtered by repository and/or status.</summary>
internal static class ListJobsEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapGet("/", Handle);

    private static List<JobDto> Handle(AppDatabase db, long? repositoryId, string? status)
    {
        var aliases = db.ListRepositories().ToDictionary(r => r.Id, r => r.Alias);
        var nonTerminal = new HashSet<string>(JobStatuses.NonTerminal);
        // "active" is served by an uncapped, repo-scoped query: filtering the globally capped ListJobs() in
        // memory could drop a long-lived non-terminal job that fell outside the newest-100 window.
        IEnumerable<JobRecord> jobs = status == "active"
            ? db.ListActiveJobs(repositoryId)
            : db.ListJobs()
                .Where(j => repositoryId is null || j.RepositoryId == repositoryId)
                .Where(j => status switch
                {
                    null or ""  => true,
                    "terminal"  => !nonTerminal.Contains(j.Status),
                    var s       => j.Status == s,
                });
        return jobs
            .Select(j => new JobDto(
                j.Id, j.RepositoryId, aliases.GetValueOrDefault(j.RepositoryId, "—"),
                j.Kind, j.Trigger, j.Status, j.Pct, j.Detail, j.StartedAt, j.FinishedAt, j.Outcome))
            .ToList();
    }
}
```

`Features/Jobs/GetJob.cs`:

```csharp
using Arius.Api.Shared.AppData;

namespace Arius.Api.Features.Jobs;

/// <summary>GET /api/jobs/{id} — the history row plus the parsed progress snapshot and warning count.</summary>
internal static class GetJobEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapGet("/{id}", Handle);

    private static IResult Handle(string id, AppDatabase db, JobStateRegistry jobStates)
    {
        var job = db.GetJob(id);
        if (job is null) return Results.NotFound();
        var repo = db.GetRepository(job.RepositoryId);

        // A job blocked in the ConfirmRehydration callback (genuinely parked at awaiting-cost, still within
        // the approval window) still has a LIVE sink here — JobRunner's method has not returned, so nothing
        // has removed it from jobStates yet. JobViewResolver reads the cost/resume the run staged on the sink
        // for exactly this case rather than hardcoding null.
        var view = JobViewResolver.Resolve(jobStates, id, job.StateJson);
        return Results.Ok(new JobDetailDto(
            job.Id, job.RepositoryId, repo?.Alias ?? "—", job.Kind, job.Trigger, job.Status,
            job.Pct, job.Detail, job.StartedAt, job.FinishedAt, job.Outcome,
            view.Snapshot, view.WarningCount, view.Cost, view.Resume));
    }
}
```

`Features/Jobs/GetJobWarnings.cs`:

```csharp
using Arius.Api.Shared.AppData;

namespace Arius.Api.Features.Jobs;

/// <summary>GET /api/jobs/{id}/warnings — the retained warning tail for a job.</summary>
internal static class GetJobWarningsEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapGet("/{id}/warnings", Handle);

    private static IResult Handle(string id, AppDatabase db, JobStateRegistry jobStates)
    {
        var job = db.GetJob(id);
        if (job is null) return Results.NotFound();

        var view = JobViewResolver.Resolve(jobStates, id, job.StateJson);
        return Results.Ok(new JobWarningsDto(view.WarningCount, view.Warnings, Truncated: view.WarningCount > view.Warnings.Count));
    }
}
```

Then `git rm src/Arius.Api/Endpoints/JobEndpoints.cs` and remove the now-empty `Endpoints/` and `Contracts/` directories.

- [ ] **Step 6: Create `Features/Jobs/JobsSlice.cs`**

```csharp
using Arius.Api.Shared.Composition;

namespace Arius.Api.Features.Jobs;

/// <summary>Registration + route mapping for the Jobs slice (runner, sink, registries, pollers, REST).</summary>
internal static class JobsSlice
{
    public static IServiceCollection AddJobs(this IServiceCollection services)
    {
        services.AddSingleton<RestoreApprovalRegistry>();
        services.AddSingleton<JobStateRegistry>();
        services.AddSingleton<JobRunner>();
        services.AddSingleton<IJobDispatcher>(sp => sp.GetRequiredService<JobRunner>());
        services.AddHostedService<RehydrationPollingService>();
        services.AddHostedService<StaleApprovalSweepService>();
        return services;
    }

    public static void MapJobs(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/jobs");
        ListJobsEndpoint.Map(group);
        GetJobEndpoint.Map(group);
        GetJobWarningsEndpoint.Map(group);
    }
}
```

- [ ] **Step 7: Final `AriusApiHost.cs`** — replace the two methods' bodies so they read exactly:

```csharp
using System.Text.Json;
using Arius.Api.Features.Accounts;
using Arius.Api.Features.Browse;
using Arius.Api.Features.Filesystem;
using Arius.Api.Features.Jobs;
using Arius.Api.Features.Repositories;
using Arius.Api.Features.Schedules;
using Arius.Api.Features.Search;
using Arius.Api.Features.Statistics;
using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;
using Arius.AzureBlob;
using Arius.Core.Shared;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Scalar.AspNetCore;

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

        // Shared mechanics
        builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysDir));
        builder.Services.AddSingleton(new AppDatabase(dbPath));
        builder.Services.AddSingleton<SecretProtector>();
        builder.Services.AddAzureBlobStorage();
        builder.Services.TryAddSingleton<IRepositoryCoreComposer, AzureRepositoryCoreComposer>();
        builder.Services.AddSingleton<RepositoryProviderRegistry>();

        // Feature slices
        builder.Services.AddAccounts();
        builder.Services.AddBrowse();
        builder.Services.AddSearch();
        builder.Services.AddJobs();
        builder.Services.AddSchedules();

        builder.Services.AddSignalR()
            .AddJsonProtocol(o => o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

        // OpenAPI document generation — Development only (mapped in MapAriusApi via Scalar).
        if (builder.Environment.IsDevelopment())
            builder.Services.AddOpenApi();

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
        api.MapAccounts();
        api.MapRepositories();
        api.MapBrowse();
        api.MapStatistics();
        api.MapJobs();
        api.MapSchedules();
        api.MapFilesystem();
        app.MapHub<JobsHub>("/hubs/arius");

        // Development-only API docs: raw document at /openapi/v1.json, Scalar UI at /scalar.
        // Mapped as real routes so they resolve before the SPA fallback below.
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.MapScalarApiReference();
        }

        app.MapFallbackToFile("index.html");

        return app;
    }
}
```

- [ ] **Step 8: Sweep for stragglers**

```bash
grep -rn 'Arius\.Api\.\(Jobs\|Hubs\|Contracts\|Endpoints\|AppData\|Composition\)' src --include='*.cs' | grep -v obj | grep -v Shared
```

Expected: no hits. Fix any stragglers the same way as above.

- [ ] **Step 9: Build, full test, commit**

```bash
dotnet build src/Arius.slnx
dotnet test src/Arius.Api.Tests && dotnet test src/Arius.Api.Integration.Tests
dotnet test src/Arius.Architecture.Tests && dotnet test src/Arius.Core.Tests
git add -A && git commit -m "refactor(api): extract Jobs slice; layer folders eliminated"
```

Verify with `git diff master --stat -- src/Arius.Api.Tests src/Arius.Api.Integration.Tests src/Arius.Api.Testing` that test-project changes are using-lines only (plus the C2 edit).

---

### Task 11: Architecture tests for the Api slices

**Files:**
- Modify: `src/Arius.Architecture.Tests/Arius.Architecture.Tests.csproj` (add Arius.Api reference)
- Create: `src/Arius.Architecture.Tests/ApiSliceTests.cs`

**Interfaces:**
- Consumes: `Arius.Api.AssemblyMarker` (root namespace `Arius.Api`), the slice namespaces produced by Tasks 2–10.

- [ ] **Step 1: Add the project reference**

```bash
dotnet add src/Arius.Architecture.Tests/Arius.Architecture.Tests.csproj reference src/Arius.Api/Arius.Api.csproj
```

- [ ] **Step 2: Create `ApiSliceTests.cs`.** Mirror the loader/violation idioms already used in `DependencyTests.cs` / `ModulithTests.cs` (same `HasNoViolations` + `DescribeViolations` pattern; adapt if the local helpers differ):

```csharp
using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using Shouldly;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Arius.Architecture.Tests;

/// <summary>
/// Slice rules for the Arius.Api host (adr-0022): Shared mechanics never depend on feature slices, and
/// feature slices never reference each other — except the documented single-hub delivery seam, where the
/// wire-frozen JobsHub delegates its foreign methods to slice-owned services.
/// </summary>
public sealed class ApiSliceTests
{
    private static readonly Architecture Architecture =
        new ArchLoader().LoadAssemblies(typeof(Api.AssemblyMarker).Assembly).Build();

    private const string FeaturesPrefix = "Arius.Api.Features.";

    // The single-hub delivery seam (adr-0022). Extend the ADR before extending this list.
    private static readonly HashSet<(string Consumer, string Target)> AllowedCrossSlice =
    [
        ("Arius.Api.Features.Jobs.JobsHub", "Arius.Api.Features.Accounts.ContainerNameService"),
        ("Arius.Api.Features.Jobs.JobsHub", "Arius.Api.Features.Browse.EntryStreamer"),
        ("Arius.Api.Features.Jobs.JobsHub", "Arius.Api.Features.Search.RepositorySearcher"),
        ("Arius.Api.Features.Jobs.JobsHub", "Arius.Api.Features.Search.SearchHitDto"),
    ];

    [Test]
    public async Task Api_Shared_Should_Not_Depend_On_Features()
    {
        IArchRule rule = Types().That().ResideInNamespace(@"Arius\.Api\.Shared.*", useRegularExpressions: true)
            .Should().NotDependOnAnyTypesThat().ResideInNamespace(@"Arius\.Api\.Features.*", useRegularExpressions: true);

        rule.HasNoViolations(Architecture).ShouldBeTrue(
            $"Arius.Api.Shared must not depend on Arius.Api.Features. Violations: {DescribeViolations(rule)}");
    }

    [Test]
    public async Task Api_Feature_Slices_Should_Not_Reference_Each_Other()
    {
        var violations = new List<string>();
        foreach (var type in Architecture.Types.Where(t => t.Namespace.FullName.StartsWith(FeaturesPrefix)))
        {
            var consumerSlice = SliceOf(type.Namespace.FullName);
            foreach (var dependency in type.Dependencies)
            {
                var targetNamespace = dependency.Target.Namespace.FullName;
                if (!targetNamespace.StartsWith(FeaturesPrefix)) continue;
                if (SliceOf(targetNamespace) == consumerSlice) continue;
                if (AllowedCrossSlice.Contains((type.FullName, dependency.Target.FullName))) continue;
                violations.Add($"{type.FullName} -> {dependency.Target.FullName}");
            }
        }

        violations.ShouldBeEmpty(
            "Api feature slices must not reference each other (cross-slice needs go through Shared/ or the documented hub seam). " +
            $"Violations: {string.Join("; ", violations.Distinct())}");
    }

    private static string SliceOf(string ns) => ns[FeaturesPrefix.Length..].Split('.')[0];

    private static string DescribeViolations(IArchRule rule) =>
        string.Join("; ", rule.Evaluate(Architecture).Where(r => !r.Passed).Select(r => r.Description));
}
```

- [ ] **Step 3: Verify the rule bites** (temporarily add `using Arius.Api.Features.Search;` and a field `private RepositorySearcher? _x;` to `Features/Accounts/ContainerNameService.cs`, run the test, expect FAIL listing `ContainerNameService -> RepositorySearcher`, then revert the edit)

```bash
dotnet test src/Arius.Architecture.Tests
```

- [ ] **Step 4: Run clean, commit**

```bash
dotnet test src/Arius.Architecture.Tests
git add -A && git commit -m "test(arch): enforce Arius.Api slice boundaries"
```

---

### Task 12: Api parity gate (route diff + hermetic e2e)

**Files:**
- Create: `docs/superpowers/plans/artifacts/2026-07-11-phase1-after-routes.txt`, `2026-07-11-phase1-after-hub.txt`

- [ ] **Step 1: Re-capture** — repeat Task 1's Step 1/2 with the `after-` filenames (hub grep now targets `src/Arius.Api/Features/Jobs/JobsHub.cs`).

- [ ] **Step 2: Diff — both must be byte-identical**

```bash
diff docs/superpowers/plans/artifacts/2026-07-11-phase1-baseline-routes.txt docs/superpowers/plans/artifacts/2026-07-11-phase1-after-routes.txt
diff docs/superpowers/plans/artifacts/2026-07-11-phase1-baseline-hub.txt docs/superpowers/plans/artifacts/2026-07-11-phase1-after-hub.txt
```

Expected: no output from either diff. Any difference = wire drift → STOP and fix before continuing.

- [ ] **Step 3: Hermetic e2e**

```bash
cd src/Arius.Web && npm ci && npm run e2e:hermetic
```

Expected: all Playwright hermetic specs pass, unmodified.

- [ ] **Step 4: Commit artifacts**

```bash
git add docs/superpowers/plans/artifacts/ && git commit -m "chore(api): phase-1 wire parity verified (route/hub diff clean)"
```

---

### Task 13: Web — split `api-models.ts` per server domain

**Files:**
- Create in `src/Arius.Web/src/app/core/api/`: `accounts.models.ts`, `repos.models.ts`, `browse.models.ts`, `search.models.ts`, `statistics.models.ts`, `jobs.models.ts`, `schedules.models.ts`, `fs.models.ts`
- Delete: `src/Arius.Web/src/app/core/api/api-models.ts`
- Modify: every importer (list in Step 3)

**Interfaces:**
- Produces: same exported names, new module paths. Split (complete):
  - `accounts.models.ts`: `AccountDto`
  - `repos.models.ts`: `RepositoryDto`, `CreateRepositoryRequest`
  - `browse.models.ts`: `SnapshotDto`, `EntryDto`, `StateFlagsDto`, `ListEntriesOptions`
  - `statistics.models.ts`: `StatisticsDto`, `TierStatisticsDto`
  - `jobs.models.ts`: `NON_TERMINAL_STATUSES`, `JobStatus`, `isNonTerminal`, `JobSnapshot`, `CostEstimateMsg`, `DoneMsg`, `JobOutcome`, `ResumeInfo`, `JobAttachState`, `JobDto`, `JobDetailDto`, `JobWarningsDto`
  - `search.models.ts`: `SearchHitDto` (imports `EntryDto` from `./browse.models`), mirroring the server's Search slice
  - `schedules.models.ts`: `ScheduleDto`
  - `fs.models.ts`: `FsEntryDto`, `FsListDto`

- [ ] **Step 1: Create the eight model files** — copy each interface/const/type verbatim from `api-models.ts` (content unchanged, including doc comments) into its file per the split above. `search.models.ts` starts with `import { EntryDto } from './browse.models';`.

- [ ] **Step 2: Delete `api-models.ts`**

- [ ] **Step 3: Update every importer.** Replace each `from './api-models'` / `'../api/api-models'` / `'../../core/api/api-models'` import with imports from the new files carrying that file's symbols. Complete importer list (from the measured usage map — paths relative to `src/app/`):
  - `core/api/api.service.ts` (split in Task 14 — for now point its import lines at the new files)
  - `core/api/realtime.service.ts` + `realtime.service.spec.ts` → `jobs.models` (`JobSnapshot`, `CostEstimateMsg`, `DoneMsg`, `JobAttachState`, `isNonTerminal`), `browse.models` (`EntryDto`, `ListEntriesOptions`), `search.models` (`SearchHitDto`)
  - `core/state/snapshot.store.ts` → `browse.models`; `core/state/job-pill.store.ts` + spec → `jobs.models`; `core/state/search.store.ts` → `search.models`; `core/state/drawer.store.spec.ts` → `jobs.models`
  - `shared/job-format.ts` + `shared/job-format.spec.ts` → `jobs.models`; `shared/cost-calculator/cost-calculator.component.ts` → `statistics.models`; `shared/folder-picker/folder-picker.component.ts` → `browse.models` (`EntryDto`) + `fs.models`
  - `features/drawer/account-drawer.component.ts` → `accounts.models`; `features/overview/overview.component.ts` → `accounts.models`; `features/repo/properties/properties-tab.component.ts` → `repos.models` + `schedules.models`; `features/repo/statistics/statistics-tab.component.ts` → `statistics.models`; `features/repo/files/files-tab.component.ts` → `browse.models`; `features/jobs/jobs.component.ts` + `features/jobs/job-detail.component.ts` → `jobs.models` (+ `schedules.models` for jobs.component); `features/search/global-search-overlay.component.ts` → `search.models`

  Then verify nothing still points at the old module:

```bash
grep -rn "api-models" src/Arius.Web/src/app && echo "STRAGGLERS" || echo "clean"
```

- [ ] **Step 4: Build, test, commit**

```bash
cd src/Arius.Web && npm run build && npm test -- --watch=false && cd ../..
git add -A && git commit -m "refactor(web): split api-models per server domain"
```

Spec files changed in import lines only (verify with `git diff --stat -- '*.spec.ts'`).

---

### Task 14: Web — split `ApiService` into per-domain API services

**Files:**
- Create in `src/Arius.Web/src/app/core/api/`: `accounts.api.ts`, `repos.api.ts`, `browse.api.ts`, `statistics.api.ts`, `jobs.api.ts`, `schedules.api.ts`, `fs.api.ts`, `health.api.ts`
- Delete: `src/Arius.Web/src/app/core/api/api.service.ts`
- Modify: all `ApiService` consumers (list in Step 3)

**Interfaces:**
- Produces (method signatures identical to today's `ApiService`, only the owning class changes):
  - `AccountsApi`: `listAccounts`, `getAccount`, `createAccount`, `updateAccount`, `deleteAccount`
  - `ReposApi`: `listRepositories`, `getRepository`, `patchRepository`, `deleteRepository`, `createRepository`
  - `BrowseApi`: `getSnapshots`
  - `StatisticsApi`: `getStatistics`
  - `JobsApi`: `getJobs`, `getJob`, `getJobWarnings`
  - `SchedulesApi`: `getSchedules`, `createSchedule`, `deleteSchedule`
  - `FsApi`: `listDirectories`
  - `HealthApi`: `getAppVersion`

- [ ] **Step 1: Create the eight API files.** Each follows this exact shape (worked example — `accounts.api.ts`; the others transplant their methods and models imports identically, bodies verbatim from `api.service.ts`):

```typescript
import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { AccountDto } from './accounts.models';

/** Typed REST client for the Accounts slice of Arius.Api. */
@Injectable({ providedIn: 'root' })
export class AccountsApi {
  private readonly http = inject(HttpClient);

  listAccounts(): Observable<AccountDto[]> {
    return this.http.get<AccountDto[]>('/api/accounts');
  }

  getAccount(id: number): Observable<AccountDto> {
    return this.http.get<AccountDto>(`/api/accounts/${id}`);
  }

  createAccount(name: string, accountKey: string | null): Observable<AccountDto> {
    return this.http.post<AccountDto>('/api/accounts', { name, accountKey });
  }

  /** Edit-account flyout: rotate the key (omit/null to keep the stored one). */
  updateAccount(id: number, body: { accountKey?: string | null }): Observable<AccountDto> {
    return this.http.patch<AccountDto>(`/api/accounts/${id}`, body);
  }

  deleteAccount(id: number): Observable<void> {
    return this.http.delete<void>(`/api/accounts/${id}`);
  }
}
```

`health.api.ts` keeps the `map` import from `rxjs` for `getAppVersion`. Method bodies must be copied verbatim — do not re-derive URLs.

- [ ] **Step 2: Delete `api.service.ts`**

- [ ] **Step 3: Update every consumer** — replace the `ApiService` import + `inject(ApiService)` with the per-domain classes, and rename call sites mechanically per the method table above. Consumers and what they need (measured):
  - `app.component.ts` → `HealthApi`
  - `core/state/snapshot.store.ts` → `BrowseApi`; `core/state/job-pill.store.ts` → `JobsApi`
  - `shared/folder-picker/folder-picker.component.ts` → `FsApi`
  - `features/overview/overview.component.ts` → `AccountsApi`, `ReposApi`; `features/repos/repos.component.ts` → `ReposApi`
  - `features/drawer/account-drawer.component.ts` → `AccountsApi`; `features/drawer/archive-restore-drawer.component.ts` → `ReposApi`
  - `features/repo/repo-detail.component.ts`, `features/repo/files/files-tab.component.ts` → `ReposApi`; `features/repo/properties/properties-tab.component.ts` → `ReposApi`, `SchedulesApi`; `features/repo/statistics/statistics-tab.component.ts` → `StatisticsApi` (+ `ReposApi` if it calls `getRepository` — follow the compiler)
  - `features/jobs/jobs.component.ts` → `JobsApi`, `ReposApi`, `SchedulesApi`; `features/jobs/job-detail.component.ts` → `JobsApi`
  - `features/wizards/add/add-repo-wizard.component.ts`, `features/wizards/create/create-repo-wizard.component.ts` → `AccountsApi`, `ReposApi`
  - Spec files that stub `ApiService` (e.g. `job-pill.store.spec.ts`, `drawer.store.spec.ts`): update the stub's type/import to the corresponding per-domain API — import/type lines only, assertions unchanged.

```bash
grep -rn "ApiService\|api.service" src/Arius.Web/src/app && echo "STRAGGLERS" || echo "clean"
```

- [ ] **Step 4: Build, test, commit**

```bash
cd src/Arius.Web && npm run build && npm test -- --watch=false && cd ../..
git add -A && git commit -m "refactor(web): dissolve ApiService into per-domain API services"
```

---

### Task 15: Web — move `snapshot.store` into `features/repo`

**Files:**
- Move: `src/Arius.Web/src/app/core/state/snapshot.store.ts` → `src/Arius.Web/src/app/features/repo/snapshot.store.ts`
- Modify: `features/repo/files/files-tab.component.ts`, `features/repo/snapshot-bar.component.ts`, `features/repo/statistics/statistics-tab.component.ts`

- [ ] **Step 1: Move and fix imports**

```bash
git mv src/Arius.Web/src/app/core/state/snapshot.store.ts src/Arius.Web/src/app/features/repo/snapshot.store.ts
```

Inside `snapshot.store.ts`, fix its own imports (`./../api/…` paths become `../../core/api/…`). Importers:
- `features/repo/snapshot-bar.component.ts`: `'../../core/state/snapshot.store'` → `'./snapshot.store'`
- `features/repo/files/files-tab.component.ts` and `features/repo/statistics/statistics-tab.component.ts`: `'../../../core/state/snapshot.store'` → `'../snapshot.store'`

(Verify the exact old specifiers with `grep -rn "snapshot.store" src/Arius.Web/src/app` first; adjust relative depth accordingly.)

- [ ] **Step 2: Build, test, commit**

```bash
cd src/Arius.Web && npm run build && npm test -- --watch=false && cd ../..
git add -A && git commit -m "refactor(web): move snapshot.store into features/repo"
```

---

### Task 16: Web — ESLint boundary rules

**Files:**
- Create: `src/Arius.Web/eslint.config.cjs`
- Modify: `src/Arius.Web/package.json` (devDependencies + `lint` script)

**Interfaces:**
- Produces: `npm run lint` enforcing: `core` imports only `core`; `shared` imports `shared`+`core`; a feature imports `core`, `shared`, and itself only; root `app.*` files may import anything.

- [ ] **Step 1: Install**

```bash
cd src/Arius.Web
npm install --save-dev eslint @typescript-eslint/parser eslint-plugin-boundaries eslint-import-resolver-typescript
```

- [ ] **Step 2: Create `eslint.config.cjs`**

```javascript
// Import-boundary rules only (adr-0022): features → shared → core, no feature-to-feature imports.
const boundaries = require('eslint-plugin-boundaries');
const tsParser = require('@typescript-eslint/parser');

module.exports = [
  {
    files: ['src/app/**/*.ts'],
    languageOptions: { parser: tsParser, ecmaVersion: 2022, sourceType: 'module' },
    plugins: { boundaries },
    settings: {
      'import/resolver': { typescript: { project: `${__dirname}/tsconfig.json` } },
      'boundaries/elements': [
        { type: 'core', pattern: 'src/app/core' },
        { type: 'shared', pattern: 'src/app/shared' },
        { type: 'feature', pattern: 'src/app/features/*', capture: ['name'] },
        { type: 'app', pattern: 'src/app/*', mode: 'file' },
      ],
    },
    rules: {
      'boundaries/element-types': ['error', {
        default: 'disallow',
        rules: [
          { from: 'core', allow: ['core'] },
          { from: 'shared', allow: ['shared', 'core'] },
          { from: 'feature', allow: ['core', 'shared', ['feature', { name: '${from.name}' }]] },
          { from: 'app', allow: ['app', 'core', 'shared', 'feature'] },
        ],
      }],
    },
  },
];
```

- [ ] **Step 3: Add the script** — in `package.json` `"scripts"`: `"lint": "eslint \"src/app/**/*.ts\""`.

- [ ] **Step 4: Verify it bites, then passes** — temporarily add `import { JobPillComponent } from '../pill/job-pill.component';` to `src/app/features/search/global-search-overlay.component.ts`; run `npm run lint`; expected: 1 error (`boundaries/element-types`). Revert the line; run `npm run lint`; expected: 0 problems. If real violations surface, they are structure bugs — fix the import (never weaken the rule) and note each in the commit message.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "build(web): add ESLint import-boundary rules"
```

---

### Task 17: ADR, docs sync, final gates, PR

**Files:**
- Create: `docs/decisions/adr-0022-vertical-slice-structure-for-host-projects.md`
- Modify: `docs/design/hosts/web.md`, `docs/guide/development.md` (only if it references the old layout)

- [ ] **Step 1: Write adr-0022** (full content):

```markdown
---
status: "accepted"
date: 2026-07-11
decision-makers: ["Wouter Van Ranst"]
consulted: ["Claude Code"]
informed: ["Arius maintainers"]
confidence: "high"
---

# Host projects adopt Core's vertical-slice structure

## Context

Arius.Core is organized as `Features/<UseCase>/` + `Shared/<Mechanic>/` (ADR-0010, ADR-0013). The web host
(`Arius.Api` + `Arius.Web`) grew organically, organized by technical layer, with a 700-line `AppDatabase`,
a mixed-responsibility hub, and centralized DTO files. This ADR records the restructure to Core's grammar
and the rules that keep it from eroding.

## Decision

**Arius.Api** is organized as `Features/<Slice>/` (Accounts, Repositories, Browse, Statistics, Search,
Filesystem, Jobs, Schedules) + `Shared/<Area>/` (AppData, Composition, Entries, Extensions). Inside a slice,
each REST operation is a self-contained REPR endpoint file owning its request/response records; each slice
exposes `Add<X>`/`Map<X>` extensions composed by `AriusApiHost`. No mediator: HTTP/SignalR already is the
host's dispatch layer.

**The single hub is a deliberate exception.** SignalR does not multiplex hubs over one connection, so
`/hubs/arius` stays one `JobsHub` (in `Features/Jobs/`). Its foreign methods are 1-line delegations to
slice-owned public services: `Accounts.ContainerNameService`, `Browse.EntryStreamer`,
`Search.RepositorySearcher`. These are the ONLY sanctioned cross-slice references, whitelisted in
`ApiSliceTests`. The `IJobDispatcher` interface in `Shared/Composition` is the Schedules→Jobs seam.

**Arius.Web** mirrors the Api's slice map inside `core/api/` (per-domain `*.api.ts` + `*.models.ts`);
feature folders own single-feature state (`snapshot.store` in `features/repo`); genuinely cross-feature
stores (drawer, job-pill, search) stay in `core/state`. Import direction: `features → shared → core`,
no feature-to-feature imports.

## Rules (mechanically enforced)

* `Arius.Api.Shared.*` never depends on `Arius.Api.Features.*` (`ApiSliceTests`).
* Api feature slices never reference each other, except the whitelisted hub seam (`ApiSliceTests`).
* Web import boundaries enforced by `eslint-plugin-boundaries` (`npm run lint`).
* Slice services injected into the public hub are `public` only for constructor accessibility
  (the ADR-0010 "public because infrastructure requires it" precedent).

## Consequences

* Good: each slice reads as one unit (endpoints + contracts + services), mirroring Core.
* Good: the wire contract is pinned by architecture tests + the route/hub parity artifacts in
  `docs/superpowers/plans/artifacts/`.
* Neutral: `AppDatabase` stays a shared mechanic; splitting it was rejected (no driver, schema-drift risk).
* Trade-off: the hub whitelist must be extended (here and in the tests) for any new cross-slice hub method.
```

- [ ] **Step 2: Update `docs/design/hosts/web.md`** — fix every path reference to the old layout. Replacement map: `Endpoints/` → `Features/<slice>/`, `Hubs/JobsHub` → `Features/Jobs/JobsHub`, `Hubs/ArchiveForwarders.cs`/`RestoreForwarders.cs` → `Features/Jobs/...`, `Jobs/JobRunner` → `Features/Jobs/JobRunner`, `Composition/RepositoryProviderRegistry` → `Shared/Composition/...`, `AppData` → `Shared/AppData`, `RepositoryEndpoints` → `the Repositories slice`, `core/api/api.service.ts` → the per-domain APIs, `core/state` store list per the moves. Check `docs/guide/development.md` for layout references and update likewise. Do not rewrite prose beyond path/name accuracy.

- [ ] **Step 3: Final gates**

```bash
dotnet build src/Arius.slnx
for p in Arius.Api.Tests Arius.Api.Integration.Tests Arius.Architecture.Tests Arius.Core.Tests; do dotnet test src/$p || exit 1; done
cd src/Arius.Web && npm run build && npm test -- --watch=false && npm run lint && npm run e2e:hermetic && cd ../..
```

All green. Real-Azure e2e (`npm run e2e`) if credentials are available; otherwise note it for CI.

- [ ] **Step 4: Commit + PR**

```bash
git add -A && git commit -m "docs: adr-0022 vertical-slice host structure + web.md path sync"
gh pr create --title "refactor: Arius.Api + Arius.Web vertical slices (phase 1 — production code)" \
  --body "Phase 1 of docs/superpowers/specs/2026-07-11-api-web-vertical-slices-design.md. Wire contract frozen (see parity artifacts); tests changed in using/import lines only, plus enumerated cleanups C1–C4."
```
