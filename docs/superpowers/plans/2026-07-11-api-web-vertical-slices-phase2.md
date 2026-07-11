# Api/Web Vertical Slices — Phase 2 (tests) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** With Phase 1's slice structure frozen, reorganize the API test suites to mirror `Features/<Slice>/` + `Shared/`, add per-slice REST contract tests, and run a quality pass — production code untouched.

**Architecture:** `Arius.Api.Tests` mirrors the production tree (the `Arius.Core.Tests` convention). Integration tests regroup by capability, keeping their scenario grain. New `Contracts/` tests pin each REPR operation's status codes and payload shapes via the in-process `AriusApiFactory`. Spec: `docs/superpowers/specs/2026-07-11-api-web-vertical-slices-design.md` (§5).

**Tech Stack:** TUnit + Shouldly, `Microsoft.AspNetCore.Mvc.Testing` (`AriusApiFactory`), `System.Net.Http.Json`.

## Global Constraints

- Branch `refactor/api-web-slices-phase2` off `master`, after the Phase 1 PR merges.
- **Zero production edits.** If an improved/new test exposes a real production bug: STOP, report it, and (only after confirmation) fix it in a separate, explicitly flagged commit.
- Playwright e2e specs (both suites) stay untouched.
- Test moves are `git mv`; test projects glob sources, so no `.csproj` edits.
- Naming for unit tests follows `Arius.Core.Tests`: `Method_Scenario_ExpectedOutcome` (e.g. `Parse_RootedPath_Throws`). Scenario/integration tests keep readable sentence names (e.g. `Health_endpoint_returns_ok`).
- TUnit idioms: `[Test]`, `public async Task`, `[Arguments(...)]` for data cases; Shouldly asserts (`.ShouldBe(...)`); TUnit `Assert.That` where a file already uses it — do not churn assertion styles file-wide.
- Gate after every task: `dotnet test src/Arius.Api.Tests && dotnet test src/Arius.Api.Integration.Tests` — 0 failed, and the test COUNT must never decrease except where a deletion is explicitly listed with its justification.
- Coverage: ADR-0011's ≥90% production-line floor is asserted by CI/Codecov on the PR — check the PR status; do not merge below the floor.
- **Declared coverage cap:** the Browse (`ListSnapshots`, `EntryStreamer`), Statistics (`GetStatistics`), and Search (`RepositorySearcher`) operations get NO new contract tests here — the scripted Core composer does not script `SnapshotsListQuery`/`StatisticsQuery`/`ListQuery`/`ISnapshotService`, so an in-proc contract test cannot exercise them. They remain pinned by the hermetic Playwright suites (`statistics.spec`, `statistics-tiers.spec`, `files.spec`, `time-travel.spec`, `search.spec`). Extending `CanonicalScenarios` to script them is possible follow-up work, deliberately out of scope.
- Every commit message ends with:
  `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`

---

### Task 1: Mirror the slice tree in `Arius.Api.Tests`

**Files (all `git mv` + namespace-line edit only):**
- `AppData/JobGuardTests.cs` → `Shared/AppData/JobGuardTests.cs`
- `AppData/JobLifecycleDbTests.cs` → `Shared/AppData/JobLifecycleDbTests.cs`
- `AppData/StatisticsCacheTests.cs` → `Shared/AppData/StatisticsCacheTests.cs`
- `Jobs/{JobSinkAggregateTests,JobSinkEtaTests,JobSinkWarningsTests,JobStateRegistryTests,RehydrationScheduleTests,RepresentationTests}.cs` → `Features/Jobs/`
- `RepositoryRegionResolutionTests.cs` → `Features/Repositories/RepositoryRegionResolutionTests.cs`

**Interfaces:** Test namespaces become `Arius.Api.Tests.Shared.AppData`, `Arius.Api.Tests.Features.Jobs`, `Arius.Api.Tests.Features.Repositories` (mirroring production, the Core.Tests convention).

- [ ] **Step 1: Move**

```bash
cd src/Arius.Api.Tests
mkdir -p Shared/AppData Features/Jobs Features/Repositories
git mv AppData/JobGuardTests.cs AppData/JobLifecycleDbTests.cs AppData/StatisticsCacheTests.cs Shared/AppData/
git mv Jobs/JobSinkAggregateTests.cs Jobs/JobSinkEtaTests.cs Jobs/JobSinkWarningsTests.cs \
       Jobs/JobStateRegistryTests.cs Jobs/RehydrationScheduleTests.cs Jobs/RepresentationTests.cs Features/Jobs/
git mv RepositoryRegionResolutionTests.cs Features/Repositories/
rmdir AppData Jobs
cd ../..
```

- [ ] **Step 2: Update each moved file's `namespace` line** to mirror its new folder (e.g. `namespace Arius.Api.Tests.AppData;` → `namespace Arius.Api.Tests.Shared.AppData;`; `namespace Arius.Api.Tests;` in `RepositoryRegionResolutionTests.cs` → `namespace Arius.Api.Tests.Features.Repositories;`). Namespace lines only — no other edits.

- [ ] **Step 3: Test and commit**

```bash
dotnet test src/Arius.Api.Tests
git add -A && git commit -m "test(api): mirror slice tree in Arius.Api.Tests"
```

Expected: same test count as before the move, 0 failed.

---

### Task 2: Relocate `JobViewResolverTests` (unit test in the integration project)

**Files:**
- Move: `src/Arius.Api.Integration.Tests/JobViewResolverTests.cs` → `src/Arius.Api.Tests/Features/Jobs/JobViewResolverTests.cs`

Justification (quality pass): it exercises `JobViewResolver`/`JobSink`/`JobStateRegistry` in-memory — no `AriusApiFactory`, no host. It belongs with the unit tests.

- [ ] **Step 1: Move + renamespace**

```bash
git mv src/Arius.Api.Integration.Tests/JobViewResolverTests.cs src/Arius.Api.Tests/Features/Jobs/JobViewResolverTests.cs
```

Change its namespace to `Arius.Api.Tests.Features.Jobs`. If it references helpers that exist only in the integration project (check compile), revert the move and instead record "kept: shared-harness dependency" in the commit message.

- [ ] **Step 2: Test and commit**

```bash
dotnet test src/Arius.Api.Tests && dotnet test src/Arius.Api.Integration.Tests
git add -A && git commit -m "test(api): move JobViewResolverTests to the unit-test project"
```

---

### Task 3: Regroup `Arius.Api.Integration.Tests` by capability

**Files (all `git mv` + namespace-line edit only):**
- `HealthSmokeTests.cs` → `Smoke/`
- `LifecycleScenarioTests.cs`, `SingleActiveJobScenarioTests.cs` → `Lifecycle/`
- `RepresentationScenarioTests.cs`, `ArchiveHubTests.cs` → `Representation/`
- `RestoreCostHandshakeTests.cs`, `ApprovalRegistryTests.cs`, `StaleApprovalSweepTests.cs` → `Approvals/`
- `ReattachScenarioTests.cs`, `ParkedCancelBroadcastTests.cs`, `ConcurrentResumeSmokeTests.cs` → `Reattach/`
- `ScenarioGateTests.cs` → `Harness/`

- [ ] **Step 1: Move**

```bash
cd src/Arius.Api.Integration.Tests
mkdir -p Smoke Lifecycle Representation Approvals Reattach
git mv HealthSmokeTests.cs Smoke/
git mv LifecycleScenarioTests.cs SingleActiveJobScenarioTests.cs Lifecycle/
git mv RepresentationScenarioTests.cs ArchiveHubTests.cs Representation/
git mv RestoreCostHandshakeTests.cs ApprovalRegistryTests.cs StaleApprovalSweepTests.cs Approvals/
git mv ReattachScenarioTests.cs ParkedCancelBroadcastTests.cs ConcurrentResumeSmokeTests.cs Reattach/
git mv ScenarioGateTests.cs Harness/
cd ../..
```

- [ ] **Step 2: Update each moved file's `namespace` to `Arius.Api.Integration.Tests.<Folder>`** and add `using Arius.Api.Integration.Tests.Harness;` where the folder change breaks the implicit same-namespace visibility of `AriusApiFactory`/`ScenarioWait` (the compiler will point at exactly these).

- [ ] **Step 3: Test and commit**

```bash
dotnet test src/Arius.Api.Integration.Tests
git add -A && git commit -m "test(api): regroup integration tests by capability"
```

---

### Task 4: Contract tests — Accounts

**Files:**
- Create: `src/Arius.Api.Integration.Tests/Contracts/AccountsContractTests.cs`

**Interfaces:**
- Consumes: `AriusApiFactory` (in-proc host + throwaway SQLite), `AccountDto` from `Arius.Api.Features.Accounts`.

- [ ] **Step 1: Write the tests**

```csharp
using System.Net;
using System.Net.Http.Json;
using Arius.Api.Features.Accounts;
using Arius.Api.Integration.Tests.Harness;
using Shouldly;

namespace Arius.Api.Integration.Tests.Contracts;

/// <summary>Pins the Accounts slice's REST contract (status codes + payload shape).</summary>
public sealed class AccountsContractTests
{
    [Test]
    public async Task Accounts_CrudRoundtrip_MatchesContract()
    {
        await using var factory = new AriusApiFactory();
        var client = factory.CreateClient();

        // Create
        var created = await client.PostAsJsonAsync("/api/accounts", new { name = "acc1", accountKey = "key-1" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var account = await created.Content.ReadFromJsonAsync<AccountDto>();
        account!.Name.ShouldBe("acc1");
        account.HasKey.ShouldBeTrue();
        account.Repositories.ShouldBe(0);

        // List + Get
        var list = await client.GetFromJsonAsync<List<AccountDto>>("/api/accounts");
        list!.Count.ShouldBe(1);
        var fetched = await client.GetFromJsonAsync<AccountDto>($"/api/accounts/{account.Id}");
        fetched!.Id.ShouldBe(account.Id);

        // Patch (rotate key; null leaves it unchanged)
        var patched = await client.PatchAsJsonAsync($"/api/accounts/{account.Id}", new { accountKey = "key-2" });
        patched.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Delete
        var deleted = await client.DeleteAsync($"/api/accounts/{account.Id}");
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetAsync($"/api/accounts/{account.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task DeleteAccount_WithRepositories_Conflicts()
    {
        await using var factory = new AriusApiFactory();
        var client = factory.CreateClient();
        factory.SeedRepository(); // seeds "fake-account" + one repo

        var accounts = await client.GetFromJsonAsync<List<AccountDto>>("/api/accounts");
        var response = await client.DeleteAsync($"/api/accounts/{accounts![0].Id}");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task GetAccount_Unknown_ReturnsNotFound()
    {
        await using var factory = new AriusApiFactory();
        var client = factory.CreateClient();

        (await client.GetAsync("/api/accounts/9999")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PatchAsJsonAsync("/api/accounts/9999", new { accountKey = "k" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.DeleteAsync("/api/accounts/9999")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
```

- [ ] **Step 2: Run — expect PASS** (these pin existing behavior; a failure means either the test's assumption or a Phase 1 regression — investigate before touching anything)

```bash
dotnet test src/Arius.Api.Integration.Tests
```

- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "test(api): Accounts REST contract tests"
```

---

### Task 5: Contract tests — Repositories

**Files:**
- Create: `src/Arius.Api.Integration.Tests/Contracts/RepositoriesContractTests.cs`

- [ ] **Step 1: Write the tests**

```csharp
using System.Net;
using System.Net.Http.Json;
using Arius.Api.Features.Accounts;
using Arius.Api.Features.Repositories;
using Arius.Api.Integration.Tests.Harness;
using Shouldly;

namespace Arius.Api.Integration.Tests.Contracts;

/// <summary>Pins the Repositories slice's REST contract. Region resolution degrades to null with the
/// scripted composer (best-effort resolve), which is exactly the contract for an unreachable container.</summary>
public sealed class RepositoriesContractTests
{
    [Test]
    public async Task Repositories_CrudRoundtrip_MatchesContract()
    {
        await using var factory = new AriusApiFactory();
        var client = factory.CreateClient();

        var account = await (await client.PostAsJsonAsync("/api/accounts", new { name = "acc1", accountKey = "k" }))
            .Content.ReadFromJsonAsync<AccountDto>();

        // Create
        var created = await client.PostAsJsonAsync("/api/repos",
            new { accountId = account!.Id, container = "cont1", alias = "repo1", passphrase = "pw", localPath = (string?)null, defaultTier = "Archive" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var repo = await created.Content.ReadFromJsonAsync<RepositoryDto>();
        repo!.Alias.ShouldBe("repo1");
        repo.Account.ShouldBe("acc1");
        repo.DefaultTier.ShouldBe("archive"); // NormalizeTier lower-cases

        // Create against a missing account
        var bad = await client.PostAsJsonAsync("/api/repos",
            new { accountId = 9999, container = "c", alias = "a", passphrase = (string?)null, localPath = (string?)null, defaultTier = (string?)null });
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // List + Get
        (await client.GetFromJsonAsync<List<RepositoryDto>>("/api/repos"))!.Count.ShouldBe(1);
        (await client.GetFromJsonAsync<RepositoryDto>($"/api/repos/{repo.Id}"))!.Id.ShouldBe(repo.Id);

        // Patch
        var patched = await client.PatchAsJsonAsync($"/api/repos/{repo.Id}", new { alias = "renamed" });
        patched.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await patched.Content.ReadFromJsonAsync<RepositoryDto>())!.Alias.ShouldBe("renamed");

        // Delete
        (await client.DeleteAsync($"/api/repos/{repo.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetAsync($"/api/repos/{repo.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
```

- [ ] **Step 2: Run + commit** (same commands as Task 4; commit message `test(api): Repositories REST contract tests`)

---

### Task 6: Contract tests — Filesystem + Schedules

**Files:**
- Create: `src/Arius.Api.Integration.Tests/Contracts/FilesystemContractTests.cs`, `Contracts/SchedulesContractTests.cs`

- [ ] **Step 1: Write `FilesystemContractTests.cs`**

```csharp
using System.Net;
using System.Net.Http.Json;
using Arius.Api.Features.Filesystem;
using Arius.Api.Integration.Tests.Harness;
using Shouldly;

namespace Arius.Api.Integration.Tests.Contracts;

public sealed class FilesystemContractTests
{
    [Test]
    public async Task FsList_ExistingDirectory_ReturnsListing()
    {
        await using var factory = new AriusApiFactory();
        var client = factory.CreateClient();
        var dir = Directory.CreateTempSubdirectory("arius-fs-contract-").FullName;
        Directory.CreateDirectory(Path.Combine(dir, "child"));

        var listing = await client.GetFromJsonAsync<FsListDto>($"/api/fs/list?path={Uri.EscapeDataString(dir)}");

        listing!.Entries.ShouldContain(e => e.Name == "child");
        listing.Parent.ShouldNotBeNull();
    }

    [Test]
    public async Task FsList_MissingDirectory_ReturnsNotFound()
    {
        await using var factory = new AriusApiFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/fs/list?path={Uri.EscapeDataString(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")))}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
```

- [ ] **Step 2: Write `SchedulesContractTests.cs`** (asserts the C1-cleaned Location header)

```csharp
using System.Net;
using System.Net.Http.Json;
using Arius.Api.Features.Schedules;
using Arius.Api.Integration.Tests.Harness;
using Shouldly;

namespace Arius.Api.Integration.Tests.Contracts;

public sealed class SchedulesContractTests
{
    [Test]
    public async Task Schedules_CrudRoundtrip_MatchesContract()
    {
        await using var factory = new AriusApiFactory();
        var client = factory.CreateClient();
        var repoId = factory.SeedRepository();

        var created = await client.PostAsJsonAsync($"/api/repos/{repoId}/schedules", new { cron = "0 3 * * *", kind = (string?)null });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        created.Headers.Location!.OriginalString.ShouldBe($"/repos/{repoId}/schedules/{(await created.Content.ReadFromJsonAsync<ScheduleDto>())!.Id}");
        var schedule = await client.GetFromJsonAsync<List<ScheduleDto>>($"/api/repos/{repoId}/schedules");
        schedule!.Count.ShouldBe(1);
        schedule[0].Kind.ShouldBe("archive"); // null kind defaults to archive
        schedule[0].Enabled.ShouldBeTrue();

        (await client.DeleteAsync($"/api/repos/{repoId}/schedules/{schedule[0].Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetFromJsonAsync<List<ScheduleDto>>($"/api/repos/{repoId}/schedules"))!.ShouldBeEmpty();
    }

    [Test]
    public async Task CreateSchedule_UnknownRepository_ReturnsNotFound()
    {
        await using var factory = new AriusApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/repos/9999/schedules", new { cron = "0 3 * * *", kind = "archive" });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
```

- [ ] **Step 3: Run + commit** (`test(api): Filesystem + Schedules REST contract tests`)

---

### Task 7: Contract tests — Jobs REST

**Files:**
- Create: `src/Arius.Api.Integration.Tests/Contracts/JobsContractTests.cs`

Scope note: job *lifecycle* is already deeply covered by the scenario suites (Lifecycle/, Reattach/, Approvals/). These tests pin only the REST read surface's empty/404 contract, which no existing test asserts directly.

- [ ] **Step 1: Write the tests**

```csharp
using System.Net;
using System.Net.Http.Json;
using Arius.Api.Features.Jobs;
using Arius.Api.Integration.Tests.Harness;
using Shouldly;

namespace Arius.Api.Integration.Tests.Contracts;

public sealed class JobsContractTests
{
    [Test]
    public async Task ListJobs_EmptyDatabase_ReturnsEmptyList()
    {
        await using var factory = new AriusApiFactory();
        var client = factory.CreateClient();

        (await client.GetFromJsonAsync<List<JobDto>>("/api/jobs"))!.ShouldBeEmpty();
        (await client.GetFromJsonAsync<List<JobDto>>("/api/jobs?status=active"))!.ShouldBeEmpty();
        (await client.GetFromJsonAsync<List<JobDto>>("/api/jobs?status=terminal"))!.ShouldBeEmpty();
    }

    [Test]
    public async Task GetJob_Unknown_ReturnsNotFound()
    {
        await using var factory = new AriusApiFactory();
        var client = factory.CreateClient();

        (await client.GetAsync("/api/jobs/no-such-job")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync("/api/jobs/no-such-job/warnings")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
```

- [ ] **Step 2: Run + commit** (`test(api): Jobs REST contract tests`)

---

### Task 8: Quality pass over the API test suites

**Files:**
- Modify: files flagged by the checks below (renames/dedup only — assertions preserved unless deleting a listed duplicate)

- [ ] **Step 1: Unit-test naming convention.** List all unit-test method names and rename any not matching `Method_Scenario_ExpectedOutcome`:

```bash
grep -rn 'public async Task ' src/Arius.Api.Tests --include='*Tests.cs' | grep -oE 'Task [A-Za-z_]+' | sort
```

Rename criteria: two-or-three segment underscore names describing subject, scenario, outcome (Core examples: `Parse_RootedPath_Throws`, `Root_RendersAsEmptyPath`). Keep a rename table in the commit message. Do NOT rename integration/scenario test methods (sentence names are that suite's convention).

- [ ] **Step 2: Duplicate/dead-test scan.** For each pair of files covering the same subject (`JobSinkAggregateTests`/`RepresentationTests`, `ArchiveHubTests`/`RepresentationScenarioTests`, `ApprovalRegistryTests`/`RestoreCostHandshakeTests`), read both and list any test that asserts a strict subset of another test's assertions on the same inputs. Delete ONLY exact-subset duplicates; record each deletion + its superset test in the commit message. When in doubt, keep both.

- [ ] **Step 3: TODO/skip scan**

```bash
grep -rn 'Skip\|TODO\|Explicit' src/Arius.Api.Tests src/Arius.Api.Integration.Tests --include='*.cs' | grep -v obj
```

For each hit: either the reason is documented inline (keep) or it isn't (fix or remove, listing it in the commit message).

- [ ] **Step 4: Run everything + commit**

```bash
dotnet test src/Arius.Api.Tests && dotnet test src/Arius.Api.Integration.Tests
git add -A && git commit -m "test(api): quality pass (naming, dedup, skip audit)"
```

---

### Task 9: Web spec audit (no restructuring expected)

- [ ] **Step 1: Verify colocation** — every `.spec.ts` sits beside its subject (Phase 1 moved them together). Check:

```bash
find src/Arius.Web/src/app -name '*.spec.ts'
```

Expected: `core/api/realtime.service.spec.ts`, `core/state/{drawer,job-pill}.store.spec.ts`, `shared/job-format.spec.ts` — each beside its subject. If any spec's subject moved without it, `git mv` the spec next to its subject and fix imports.

- [ ] **Step 2: Run + commit (only if anything changed)**

```bash
cd src/Arius.Web && npm test -- --watch=false && npm run lint && cd ../..
git add -A && git commit -m "test(web): spec colocation audit"
```

---

### Task 10: Final gates, docs freeze, PR

- [ ] **Step 1: Full gates**

```bash
dotnet build src/Arius.slnx
for p in Arius.Api.Tests Arius.Api.Integration.Tests Arius.Architecture.Tests Arius.Core.Tests; do dotnet test src/$p || exit 1; done
cd src/Arius.Web && npm run build && npm test -- --watch=false && npm run lint && npm run e2e:hermetic && cd ../..
```

- [ ] **Step 2: Docs** — invoke the repo's `update-docs` skill with `--base master --intent docs/superpowers/specs/2026-07-11-api-web-vertical-slices-design.md` so the docs tree and history freeze reflect both phases.

- [ ] **Step 3: PR**

```bash
gh pr create --title "refactor: Arius.Api + Arius.Web vertical slices (phase 2 — tests)" \
  --body "Phase 2 of docs/superpowers/specs/2026-07-11-api-web-vertical-slices-design.md. Production code untouched; test suites mirror the slice tree; per-slice REST contract tests added; quality pass applied (rename/dedup tables in commit messages). Coverage gate: see Codecov status (ADR-0011)."
```

Check the CI/Codecov status: ≥90% production line coverage must hold before merge.
