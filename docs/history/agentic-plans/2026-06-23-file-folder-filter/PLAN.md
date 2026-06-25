# Configurable file/folder exclusions for ArchiveCommandHandler

## Context

`ArchiveCommandHandler` currently enumerates **every** file and folder under the root directory
(`LocalFileEnumerator.Enumerate` → `RelativeFileSystem.EnumerateFiles()`, a flat recursive walk).
The previous version of Arius excluded noise that should never be backed up:

- **Directories**: `@eaDir`, `eaDir`, `SynoResource` (Synology NAS thumbnail/metadata folders)
- **Files**: `autorun.ini`, `thumbs.db`, `.ds_store`
- Anything carrying the **`System`** file attribute (`Hidden` was present but commented out)

These were hardcoded `HashSet`s. We want them **configurable**, defined in **one central place in
`Arius.Core`** (an embedded `appsettings.json`) so the CLI, API, and Migration hosts all inherit the
same list and can never drift. We keep the System-attribute skip and add a configurable Hidden toggle.

Secondary requirement: if a file was backed up in a previous run, it must **disappear** from the
snapshot once excluded. This is **already true** — confirmed below — and we lock it in with a test.

### Confirmed during exploration

| Fact | Location |
|------|----------|
| Enumeration entry point | `src/Arius.Core/Features/ArchiveCommand/LocalFileEnumerator.cs` `Enumerate(LocalDirectory)` |
| Low-level FS primitives already exist for a pruning walk | `RelativeFileSystem.EnumerateDirectories(path)` (top-only) + `EnumerateFiles(path, SearchOption.TopDirectoryOnly)` |
| `RelativePath` API | `.Name` (PathSegment), `.Segments`, `.Parent`; no attribute access yet |
| `AddArius(...)` is the **single** composition point for **all** hosts | `src/Arius.Core/ServiceCollectionExtensions.cs`; called by CLI `CliBuilder`, API `RepositoryProviderRegistry.BuildAsync:136`, Migration `Program.cs:54` |
| `ArchiveCommandHandler` is DI-constructed via a factory | `ServiceCollectionExtensions.cs:99-111`; also built manually in `RepositoryTestFixture.CreateArchiveHandler()` |
| Arius.Core has no `Microsoft.Extensions.Configuration.*` refs yet — **approved to add** | `src/Arius.Core/Arius.Core.csproj` (uses CPM via `Directory.Packages.props`) |
| **Snapshot is rebuilt purely from the current run's enumerated files** (ephemeral staging, no merge with prior snapshot) | `FileTreeBuilder.SynchronizeAsync` + `FileTreeStagingSession` (deletes staging each run); proven by `RoundtripTests.Archive_FileDeleted_AbsentFromNewSnapshot_PresentInOld` |
| Tests: TUnit + NSubstitute + Shouldly, real temp dirs | `LocalFileEnumeratorTests`, `RoundtripTests`, `PipelineFixture`, `RepositoryTestFixture` |

## Design

### 1. `FileExclusionOptions` (POCO) — Arius.Core
New file `src/Arius.Core/Features/ArchiveCommand/FileExclusionOptions.cs`. Plain bindable options
class consumed via the **Options pattern** — no static loader.

```csharp
public sealed class FileExclusionOptions
{
    public const string SectionName = "Arius:Exclusions";

    public List<string> ExcludedDirectoryNames { get; set; } = new();
    public List<string> ExcludedFileNames      { get; set; } = new();
    public bool         ExcludeSystemEntries   { get; set; } = true;   // matches old behavior
    public bool         ExcludeHiddenEntries   { get; set; } = false;  // old code had Hidden commented out
}
```
- `get; set;` + `List<string>` are the safest shape for the `Microsoft.Extensions.Configuration` binder.
- Defaults are supplied by the embedded JSON (#2), not by these initializers, so there is a single
  source of truth.

### 2. Central embedded `appsettings.json` — Arius.Core
New file `src/Arius.Core/appsettings.json`, marked `<EmbeddedResource>` in `Arius.Core.csproj`
(embedded → **no output-dir collision** with `Arius.Api`/`Arius.Explorer` appsettings.json, ships
inside the DLL, works for the global CLI tool with no loose files).

```json
{
  "Arius": {
    "Exclusions": {
      "ExcludedDirectoryNames": ["@eaDir", "eaDir", "SynoResource"],
      "ExcludedFileNames":      ["autorun.ini", "thumbs.db", ".ds_store"],
      "ExcludeSystemEntries":   true,
      "ExcludeHiddenEntries":   false
    }
  }
}
```
This embedded file is the **base configuration layer**, fed into the Options pipeline in `AddArius`
(#7) — there is no static `LoadDefaults()`; the handler never reads config directly. A tiny internal
helper reads the manifest stream:
```csharp
internal static IConfiguration EmbeddedCoreDefaults()
{
    var stream = typeof(FileExclusionOptions).Assembly
        .GetManifestResourceStream("Arius.Core.appsettings.json")!;
    return new ConfigurationBuilder().AddJsonStream(stream).Build();
}
```
**Add to Arius.Core** (`Microsoft.Extensions.Configuration.*`, approved): `Microsoft.Extensions.Configuration.Json`,
`Microsoft.Extensions.Configuration.Binder` (+ transitively `...Configuration.Abstractions`), and
`Microsoft.Extensions.Options` (for `AddOptions`/`IOptions`). Versions go in `Directory.Packages.props`
(CPM, ~`10.0.x` to match the other `Microsoft.Extensions.*` entries); `<PackageReference>`s go in
`Arius.Core.csproj`.

### 3. `FileExclusionFilter` — Arius.Core
New file `src/Arius.Core/Features/ArchiveCommand/FileExclusionFilter.cs` (internal).

```csharp
internal sealed class FileExclusionFilter
{
    private readonly HashSet<string> _dirs;   // OrdinalIgnoreCase
    private readonly HashSet<string> _files;  // OrdinalIgnoreCase
    private readonly bool _excludeSystem, _excludeHidden;

    public FileExclusionFilter(FileExclusionOptions options) { ... }

    /// True when any attribute rule is active — lets the enumerator skip the stat otherwise.
    public bool RequiresAttributes => _excludeSystem || _excludeHidden;

    public bool ShouldExcludeDirectory(PathSegment name, FileAttributes attrs);
    public bool ShouldExcludeFile(PathSegment name, FileAttributes attrs);
    // name match via _dirs/_files.Contains(name.ToString()); attribute match via attrs flags.
}
```

### 4. `RelativeFileSystem.GetAttributes` — Arius.Core
Add to `src/Arius.Core/Shared/FileSystem/RelativeFileSystem.cs` (consistent with existing
`GetFileSize`/`GetTimestamps`):
```csharp
public FileAttributes GetAttributes(RelativePath path) => File.GetAttributes(root.Resolve(path));
```
(`File.GetAttributes` works for both files and directories.)

### 5. Pruning recursive walk in `LocalFileEnumerator` — Arius.Core
Restructure `Enumerate` to descend directory-by-directory and **prune excluded subtrees** (efficient
on huge `@eaDir` folders; required for directory-attribute checks). The existing per-file
symlink-validity check and binary/pointer pairing logic are **unchanged** — only the *source* of
relative paths changes from the flat `EnumerateFiles()` to a pruned recursion.

```csharp
public IEnumerable<FilePair> Enumerate(LocalDirectory rootDirectory, FileExclusionFilter? filter = null)
// filter == null  ⇒ exclude nothing (preserves existing test semantics)
```
Recursion per directory `dir`:
- Files via `fs.EnumerateFiles(dir, SearchOption.TopDirectoryOnly)`: if
  `filter.ShouldExcludeFile(name, attrs)` → log + `continue`; else run existing symlink + pairing logic.
- Subdirs via `fs.EnumerateDirectories(dir)`: if `filter.ShouldExcludeDirectory(name, attrs)` →
  log (`"[scan] excluding directory: {RelPath}"`) + skip subtree; else recurse.
- Only call `fs.GetAttributes(...)` when `filter.RequiresAttributes` (avoid a stat per entry when
  attribute rules are off).

> Symlinked-directory cycles: parity with today's `Directory.EnumerateFiles(AllDirectories)` (which
> also follows them); no new cycle handling in scope.

### 6. Inject the filter into `ArchiveCommandHandler` — Arius.Core
Add a `FileExclusionFilter exclusionFilter` parameter to **both** constructors
(`ArchiveCommandHandler.cs:113` public, `:129` internal); store it; pass it to
`enumerator.Enumerate(LocalDirectory.Parse(opts.RootDirectory), filter)` at the Stage-1 call
(`ArchiveCommandHandler.cs:280`). The handler depends on the filter, not on `IOptions<>` — Options
plumbing stays at the composition layer.

### 7. Register via the Options pattern in `AddArius` (the central point) — Arius.Core
`src/Arius.Core/ServiceCollectionExtensions.cs`:
- Add optional param: `AddArius(..., IConfiguration? configuration = null)`.
- Build a config from Core's embedded defaults, optionally layered with the host config:
  ```csharp
  var config = new ConfigurationBuilder()
      .AddConfiguration(EmbeddedCoreDefaults())   // central base (#2)
      .AddConfiguration(configuration ?? new ConfigurationBuilder().Build())  // host override on top
      .Build();
  services.AddOptions<FileExclusionOptions>().Bind(config.GetSection(FileExclusionOptions.SectionName));
  services.AddSingleton<FileExclusionFilter>(sp =>
      new FileExclusionFilter(sp.GetRequiredService<IOptions<FileExclusionOptions>>().Value));
  ```
- In the `ArchiveCommandHandler` factory (`:99-111`) add `sp.GetRequiredService<FileExclusionFilter>()`.

Result: CLI, API, and Migration all inherit the **same** embedded defaults with **zero host changes**.

### 8. Optional host override (no drift unless opted in)
A host overrides simply by passing its `IConfiguration` to `AddArius` — the host's `Arius:Exclusions`
section (whether from its existing `appsettings.json` or an optional `appsettings.core.json` layer)
binds on top of Core's defaults:
- **API** (`RepositoryProviderRegistry.BuildAsync`): pass the app's `IConfiguration` into `AddArius(...)`.
  Absent the section, Core defaults stand.
- Do **not** add `Arius:Exclusions` sections to any host by default — the central defaults stand alone.

## Tests (TUnit + Shouldly + NSubstitute)

**Unit — `FileExclusionFilterTests.cs`** (new, `Arius.Core.Tests/Features/ArchiveCommand/`):
- Dir/file name match is **case-insensitive** (`@EADIR`, `Thumbs.DB`).
- `ShouldExclude*` with synthetic `FileAttributes.System` / `.Hidden` / `.Normal` respecting the
  toggles (System on by default, Hidden off by default) — **platform-independent** (no real attrs).
- Empty options exclude nothing; `RequiresAttributes` reflects the toggles.

**Unit — embedded-defaults binding** (in the above or a small test):
- Bind `EmbeddedCoreDefaults().GetSection(FileExclusionOptions.SectionName).Get<FileExclusionOptions>()`:
  exact expected names + `ExcludeSystemEntries=true`, `ExcludeHiddenEntries=false` (guards the
  JSON↔POCO contract and that the embedded resource is found/named correctly).

**Unit — `LocalFileEnumeratorTests.cs`** (extend existing; existing tests stay green via `filter=null`):
- `@eaDir/thumb.jpg` and nested `photos/@eaDir/x.jpg` → whole subtree pruned; sibling `photos/real.jpg` kept.
- `thumbs.db`, `.DS_Store`, `autorun.ini` (mixed case) → not yielded; normal files yielded.
- Pruned/excluded entries don't break pointer pairing (excluded dir containing binary+pointer; a
  `thumbs.db` alongside its pointer).
- Hidden toggle via a dotfile (`.secret`) — Unix maps dotfiles to `Hidden`; assert excluded only when
  `ExcludeHiddenEntries=true`. (System-attribute enumerator behavior is covered at the filter level;
  real System attrs don't exist on Unix.)
- Custom options exclude per the supplied lists.

**Integration/roundtrip** (`Arius.Integration.Tests/Pipeline/RoundtripTests.cs`, mirror existing
patterns; thread `FileExclusionOptions` through `PipelineFixture.ArchiveAsync`/`CreateArchiveHandler`):
- **Excluded files never enter the snapshot**: source with normal files + `@eaDir/thumb.jpg` +
  `thumbs.db`; archive with defaults; restore; assert normal files restored, excluded absent.
- **Disappears in a new run** (the explicit requirement): archive run 1 with **no** exclusions
  (file present → restore v1 shows it); archive run 2 with the file now excluded over the **same**
  container/caches; restore latest → file **absent**, restore v1 → still present. Direct analogue of
  the existing `Archive_FileDeleted_AbsentFromNewSnapshot_PresentInOld`.

**DI — `AddArius` registration** (in an existing Core.Tests DI/registration test, or new small test):
- `AddArius(...)` with no `configuration` → resolved `IOptions<FileExclusionOptions>.Value` /
  `FileExclusionFilter` reflect the embedded defaults.
- Passing an `IConfiguration` with an `Arius:Exclusions` section → the host values win (override layered on top).

## Files

**Modify**
- `Directory.Packages.props` — `<PackageVersion>` for `Microsoft.Extensions.Configuration.Json`, `.Binder`, `Microsoft.Extensions.Options`
- `src/Arius.Core/Arius.Core.csproj` — `<EmbeddedResource Include="appsettings.json" />` + the three `<PackageReference>`s
- `src/Arius.Core/ServiceCollectionExtensions.cs` — optional `IConfiguration` param + `AddOptions`/`Bind` + `FileExclusionFilter` singleton + factory wire-up
- `src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs` — ctor `FileExclusionFilter` param + pass at `:280`
- `src/Arius.Core/Features/ArchiveCommand/LocalFileEnumerator.cs` — pruning walk + filter param
- `src/Arius.Core/Shared/FileSystem/RelativeFileSystem.cs` — `GetAttributes`
- `src/Arius.Tests.Shared/Fixtures/RepositoryTestFixture.cs` — `CreateArchiveHandler` optional `FileExclusionOptions` (wraps into a `FileExclusionFilter`; null → no exclusions)
- `src/Arius.Integration.Tests/Pipeline/PipelineFixture.cs` — `ArchiveAsync` optional `FileExclusionOptions`
- *(optional)* `src/Arius.Api/Composition/RepositoryProviderRegistry.cs` — pass `IConfiguration` into `AddArius`

**Create**
- `src/Arius.Core/appsettings.json` (embedded central defaults)
- `src/Arius.Core/Features/ArchiveCommand/FileExclusionOptions.cs`
- `src/Arius.Core/Features/ArchiveCommand/FileExclusionFilter.cs`
- `src/Arius.Core.Tests/Features/ArchiveCommand/FileExclusionFilterTests.cs`
- new tests in `LocalFileEnumeratorTests.cs` + `RoundtripTests.cs`

## Verification
1. `dotnet build src/Arius.Core` — confirms the new `Microsoft.Extensions.Configuration.*` refs resolve and the `appsettings.json` resource embeds (`LoadDefaults()` finds `Arius.Core.appsettings.json`).
2. `dotnet test src/Arius.Core.Tests` — filter, options-load, enumerator (pruning/names/attrs) tests pass; existing enumerator tests still green.
3. `dotnet test src/Arius.Integration.Tests` — roundtrip exclusion + disappearance tests pass (uses Azurite).
4. Manual smoke (optional): run the CLI `archive` against a folder containing `@eaDir/…` and `thumbs.db`; confirm logs show the directory excluded and a follow-up restore omits them — with **no** CLI config file present (central Core defaults applied).
