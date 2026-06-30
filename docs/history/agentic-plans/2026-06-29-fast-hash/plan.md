# Fast-hash + hashcache Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an opt-in `--fast-hash` that skips re-reading unchanged binaries during `archive`, backed by a local SQLite hashcache, and make `--write-pointers` the opt-in (pointers off by default).

**Architecture:** A per-repository `HashCacheService` (facade over a SQLite `HashCacheLocalStore`, mirroring `ChunkIndexService`/`ChunkIndexLocalStore`) answers a per-file verdict: reuse the cached `ContentHash` when the live file is unchanged, else signal a full re-hash. The verdict ladder is `cache-miss / fp_algo bump → full · size changed → full · (dev+inode+ctime) all match → reuse (no reads) · else sparse-fingerprint decides`. Change-signals come from a new `RelativeFileSystem.TryGetChangeSignals` (platform-specific; `null` on network/unsupported filesystems → sparse-fingerprint floor). The cache is validated against the live file, never a pointer; on loss it rebuilds by full-hashing.

**Tech Stack:** C# / .NET 10, Microsoft.Data.Sqlite, TUnit + Shouldly tests, System.CommandLine (CLI), Angular (Web). Native interop: `Mono.Posix.NETStandard` for POSIX `stat`/`statfs`; `GetFileInformationByHandleEx`/`GetDriveType` P/Invoke on Windows.

## Global Constraints

- **Target framework:** `net10.0` (from `Directory.Build.props`).
- **Hashes are typed.** Use `ContentHash` (never `string`/generic hash). Persisted form is canonical lowercase hex; `ContentHash.ToString()` / `ContentHash.Parse(string)` at the SQLite boundary. (`ADR-0003`.)
- **Local IO goes through `RelativeFileSystem`**, not raw `System.IO`. (`ADR-0008`.)
- **Non-test classes are `internal`**; use `InternalsVisibleTo` for tests (already configured for `Arius.Core.Tests`).
- **One top-level class per file; filename matches the class.**
- **TUnit tests:** `[Test]`, `[Arguments(...)]`, Shouldly `.ShouldBe(...)`. Run with `dotnet test --project <csproj> --treenode-filter "/*/*/<Class>/*"` (NOT `--filter`). Use `FakeLogger<T>` not `NullLogger<T>` in tests.
- **Pre-release, clean break:** no back-compat shims for persisted formats; existing pointer files (v5 JSON, v7 bare hex) are unchanged by this work — **no pointer-format change, no migration.**
- **Conventional commits**, one per task step where indicated. End commit messages with the `Co-Authored-By` trailer used by this repo.
- **The default path is unchanged:** without `--fast-hash`, hashing behaviour is byte-for-byte identical to today.
- **Spec:** `docs/superpowers/specs/2026-06-29-fast-hash-design.md` — the source of truth for behaviour; this plan implements it.

---

## File Structure

**Phase 1 — fast-hash core (Core + CLI)**
- `src/Arius.Core/Shared/HashCache/SparseFingerprint.cs` — *new.* Deterministic region selection + combined fingerprint; sequential sampler for the inline (zero-extra-IO) path and a seek-based computer for the floor path.
- `src/Arius.Core/Shared/HashCache/FileChangeSignals.cs` — *new.* `(Ctime, Inode, Dev, SignalSet)` value + the `SignalSet` discriminator.
- `src/Arius.Core/Shared/HashCache/HashCacheEntry.cs` — *new.* One persisted row.
- `src/Arius.Core/Shared/HashCache/HashCacheLocalStore.cs` — *new.* SQLite schema + upsert/lookup/delete (mirrors `ChunkIndexLocalStore`).
- `src/Arius.Core/Shared/HashCache/IHashCacheService.cs` — *new.* Facade interface.
- `src/Arius.Core/Shared/HashCache/HashCacheService.cs` — *new.* Verdict ladder + record.
- `src/Arius.Core/Shared/HashCache/FastHashResult.cs` — *new.* Verdict return type.
- `src/Arius.Core/Shared/RepositoryLocalStatePaths.cs` — *modify.* Add `GetHashCacheRoot`.
- `src/Arius.Core/Shared/FileSystem/RelativeFileSystem.cs` — *modify.* Add `TryGetChangeSignals`.
- `src/Arius.Core/Shared/FileSystem/NativeFileSignals.cs` — *new.* Platform P/Invoke (POSIX stat/statfs, Windows file-info/drive-type) behind a single static API.
- `src/Arius.Core/Features/ArchiveCommand/ArchiveCommand.cs` — *modify.* Add `FastHash`.
- `src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs` — *modify.* Inject `IHashCacheService`; Stage-2 integration; per-decision + summary logging.
- `src/Arius.Core/ServiceCollectionExtensions.cs` — *modify.* Register `IHashCacheService`; pass to handler.
- `src/Arius.Cli/Commands/Archive/ArchiveVerb.cs` — *modify.* Add `--fast-hash`.

**Phase 2 — pointer default flip + hosts**
- `src/Arius.Core/Features/ArchiveCommand/ArchiveCommand.cs` — *modify.* Rename `NoPointers` → `WritePointers` (default `false`).
- `src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs` — *modify.* Pointer logic + `--remove-local` implies write-pointers.
- `src/Arius.Cli/Commands/Archive/ArchiveVerb.cs` — *modify.* Replace `--no-pointers` with `--write-pointers`; drop the mutual-exclusion error.
- `src/Arius.Web/src/app/features/drawer/archive-restore-drawer.component.ts` + `src/Arius.Web/src/app/core/state/drawer.store.ts` — *modify.* Radio + fast-hash toggle.
- `src/Arius.Web/e2e/specs/archive.spec.ts` — *modify.* Update UI expectations.
- `src/Arius.Api/Jobs/JobRunner.cs` — *modify.* Align host defaults.

**Docs**
- `docs/decisions/adr-0021-opt-in-change-detection-hashcache.md` — *new.*
- `docs/design/core/shared/hashcache.md` — *new.*
- `docs/design/core/features/archive-command.md`, `docs/glossary.md`, `AGENTS.md`, `README.md` — *modify.*

---

# Phase 1 — fast-hash core

### Task 1: Hashcache repository path

**Files:**
- Modify: `src/Arius.Core/Shared/RepositoryLocalStatePaths.cs`
- Test: `src/Arius.Core.Tests/Shared/RepositoryLocalStatePathsTests.cs` (create if absent)

**Interfaces:**
- Produces: `RepositoryLocalStatePaths.GetHashCacheRoot(string accountName, string containerName) → LocalDirectory` = `~/.arius/{account}-{container}/hash`.

- [ ] **Step 1: Write the failing test**

```csharp
using Arius.Core.Shared;

namespace Arius.Core.Tests.Shared;

public class RepositoryLocalStatePathsTests
{
    [Test]
    public void GetHashCacheRoot_IsUnderRepositoryRoot_NamedHash()
    {
        var root = RepositoryLocalStatePaths.GetHashCacheRoot("acct", "cont");
        root.ToString().Replace('\\', '/').ShouldEndWith(".arius/acct-cont/hash");
    }
}
```

- [ ] **Step 2: Run test, verify it fails**

Run: `dotnet test --project src/Arius.Core.Tests --treenode-filter "/*/*/RepositoryLocalStatePathsTests/*"`
Expected: FAIL — `GetHashCacheRoot` does not exist (compile error).

- [ ] **Step 3: Implement**

In `RepositoryLocalStatePaths.cs`, beside the other `PathSegment` fields add:

```csharp
    private static readonly PathSegment hashCache = PathSegment.Parse("hash");
```

and beside the other `Get*CacheRoot` members add:

```csharp
    internal static LocalDirectory GetHashCacheRoot(string accountName, string containerName) => GetRepositoryRoot(accountName, containerName) / hashCache;
```

- [ ] **Step 4: Run test, verify it passes**

Run: `dotnet test --project src/Arius.Core.Tests --treenode-filter "/*/*/RepositoryLocalStatePathsTests/*"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Core/Shared/RepositoryLocalStatePaths.cs src/Arius.Core.Tests/Shared/RepositoryLocalStatePathsTests.cs
git commit -m "feat(hashcache): add GetHashCacheRoot repository path"
```

---

### Task 2: Sparse fingerprint

**Files:**
- Create: `src/Arius.Core/Shared/HashCache/SparseFingerprint.cs`
- Test: `src/Arius.Core.Tests/Shared/HashCache/SparseFingerprintTests.cs`

**Interfaces:**
- Produces:
  - `SparseFingerprint.Algo` (`const int` = `1`) — the `fp_algo` version.
  - `SparseFingerprint.Regions(long size) → IReadOnlyList<(long Offset, int Length)>` — deterministic, non-decreasing offsets; empty when `size == 0`.
  - `SparseFingerprint.ComputeBySeeking(RelativeFileSystem fs, RelativePath path, long size) → byte[]` — seeks each region, hashes `size ‖ region-bytes`, returns a 32-byte digest. (Floor path.)
  - `SparseFingerprint.Sampler` — a forward-only sink: `Capture(long position, ReadOnlySpan<byte> buffer)` accumulates the sampled regions as a sequential stream passes; `Finish() → byte[]`. (Inline path; used by the read-through stream in Task 7.)
- Consumes: `RelativeFileSystem.OpenRead` (Task — existing).

Constants (tunable; benchmark on the Synology image before locking): block `B = 256 * 1024`; stride `S = 1L << 30` (1 GiB); `Kmin = 4`; `Kmax = 64`. `K = clamp(ceil(size / S), Kmin, Kmax)`. Files with `size <= K*B` are read whole (one region `[0, size)`).

- [ ] **Step 1: Write the failing tests**

```csharp
using Arius.Core.Shared;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.HashCache;

namespace Arius.Core.Tests.Shared.HashCache;

public class SparseFingerprintTests
{
    [Test]
    public void Regions_SmallFile_IsWholeFile()
    {
        var regions = SparseFingerprint.Regions(1000);
        regions.Count.ShouldBe(1);
        regions[0].ShouldBe((0L, 1000));
    }

    [Test]
    public void Regions_HugeFile_IsCappedAt64Blocks_HeadAndTailIncluded()
    {
        long size = 3L * 1024 * 1024 * 1024 * 1024; // 3 TiB
        var regions = SparseFingerprint.Regions(size);
        regions.Count.ShouldBe(64);
        regions[0].Offset.ShouldBe(0);
        regions[^1].Offset.ShouldBe(size - 256 * 1024);
        // offsets strictly non-decreasing
        for (var i = 1; i < regions.Count; i++)
            regions[i].Offset.ShouldBeGreaterThanOrEqualTo(regions[i - 1].Offset);
    }

    [Test]
    public void ComputeBySeeking_IsDeterministic_AndChangesWhenSampledRegionChanges()
    {
        var (fs, path) = WriteTempFile(Enumerable.Range(0, 2_000_000).Select(i => (byte)i).ToArray());
        var size = fs.GetFileSize(path);

        var fp1 = SparseFingerprint.ComputeBySeeking(fs, path, size);
        var fp2 = SparseFingerprint.ComputeBySeeking(fs, path, size);
        fp1.ShouldBe(fp2);
        fp1.Length.ShouldBe(32);

        // Flip a byte in the head region → fingerprint must change.
        var bytes = fs.ReadAllBytes(path);
        bytes[0] ^= 0xFF;
        fs.WriteAllBytes(path, bytes);
        SparseFingerprint.ComputeBySeeking(fs, path, size).ShouldNotBe(fp1);
    }

    [Test]
    public void Sampler_MatchesSeekingFingerprint_ForSameContent()
    {
        var data = Enumerable.Range(0, 2_000_000).Select(i => (byte)(i * 7)).ToArray();
        var (fs, path) = WriteTempFile(data);
        var size = fs.GetFileSize(path);

        var seekFp = SparseFingerprint.ComputeBySeeking(fs, path, size);

        // Drive the sampler the way a sequential read would.
        var sampler = new SparseFingerprint.Sampler(size);
        var pos = 0;
        const int chunk = 64 * 1024;
        while (pos < data.Length)
        {
            var len = Math.Min(chunk, data.Length - pos);
            sampler.Capture(pos, data.AsSpan(pos, len));
            pos += len;
        }
        sampler.Finish().ShouldBe(seekFp);
    }

    private static (RelativeFileSystem fs, RelativePath path) WriteTempFile(byte[] data)
    {
        var dir  = LocalDirectory.Parse(Path.Combine(Path.GetTempPath(), $"arius-fp-{Guid.NewGuid():N}"));
        var fs   = new RelativeFileSystem(dir);
        var path = RelativePath.Parse("blob.bin");
        fs.WriteAllBytes(path, data);
        return (fs, path);
    }
}
```

- [ ] **Step 2: Run tests, verify they fail**

Run: `dotnet test --project src/Arius.Core.Tests --treenode-filter "/*/*/SparseFingerprintTests/*"`
Expected: FAIL — `SparseFingerprint` does not exist.

- [ ] **Step 3: Implement `SparseFingerprint`**

```csharp
using System.Security.Cryptography;
using Arius.Core.Shared.FileSystem;

namespace Arius.Core.Shared.HashCache;

/// <summary>
/// Deterministic spot-hash fingerprint of a file used by <c>--fast-hash</c> to detect content
/// change without re-reading the whole file. Regions are derived from the file size, so the same
/// regions are re-sampled across runs (size is the precondition for any comparison).
/// </summary>
[SharedWithinAssembly]
internal static class SparseFingerprint
{
    public const int Algo = 1;

    private const int  BlockSize = 256 * 1024;     // B
    private const long Stride    = 1L << 30;        // S (1 GiB)
    private const int  MinBlocks = 4;
    private const int  MaxBlocks = 64;

    /// <summary>Deterministic (offset,length) regions for a file of <paramref name="size"/> bytes.</summary>
    public static IReadOnlyList<(long Offset, int Length)> Regions(long size)
    {
        if (size <= 0)
            return [];

        var k = (int)Math.Clamp((size + Stride - 1) / Stride, MinBlocks, MaxBlocks);

        // Small files: a single whole-file region (one sequential read, no seeks).
        if (size <= (long)k * BlockSize)
            return [(0L, (int)size)];

        var regions = new (long, int)[k];
        for (var i = 0; i < k; i++)
        {
            var offset = (long)Math.Floor((double)i * (size - BlockSize) / (k - 1));
            regions[i] = (offset, BlockSize);
        }
        return regions;
    }

    /// <summary>Reads each region by seeking and returns SHA-256 over <c>size ‖ region-bytes</c>.</summary>
    public static byte[] ComputeBySeeking(RelativeFileSystem fs, RelativePath path, long size)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(BitConverter.GetBytes(size));

        using var stream = fs.OpenRead(path);
        var buffer = new byte[BlockSize];
        foreach (var (offset, length) in Regions(size))
        {
            stream.Seek(offset, SeekOrigin.Begin);
            stream.ReadExactly(buffer, 0, length);
            sha.AppendData(buffer, 0, length);
        }
        return sha.GetHashAndReset();
    }

    /// <summary>
    /// Forward-only sink that captures the fingerprint regions as a sequential read passes, so the
    /// fingerprint costs zero extra I/O when the file is already being fully hashed.
    /// </summary>
    public sealed class Sampler
    {
        private readonly long                            _size;
        private readonly IReadOnlyList<(long Off, int Len)> _regions;
        private readonly byte[][]                        _captured;

        public Sampler(long size)
        {
            _size     = size;
            _regions  = Regions(size);
            _captured = _regions.Select(r => new byte[r.Len]).ToArray();
        }

        /// <summary>Offer the bytes read at <paramref name="position"/>; overlapping region bytes are copied out.</summary>
        public void Capture(long position, ReadOnlySpan<byte> buffer)
        {
            for (var i = 0; i < _regions.Count; i++)
            {
                var (off, len) = _regions[i];
                var from = Math.Max(position, off);
                var to   = Math.Min(position + buffer.Length, off + len);
                if (from >= to)
                    continue;
                var srcStart = (int)(from - position);
                var dstStart = (int)(from - off);
                buffer.Slice(srcStart, (int)(to - from)).CopyTo(_captured[i].AsSpan(dstStart));
            }
        }

        public byte[] Finish()
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            sha.AppendData(BitConverter.GetBytes(_size));
            foreach (var region in _captured)
                sha.AppendData(region);
            return sha.GetHashAndReset();
        }
    }
}
```

> Note: `[SharedWithinAssembly]` is the repo's marker attribute used on `Shared/` helpers (see `PointerFileFormat`/`RelativeFileSystem`).

- [ ] **Step 4: Run tests, verify they pass**

Run: `dotnet test --project src/Arius.Core.Tests --treenode-filter "/*/*/SparseFingerprintTests/*"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Core/Shared/HashCache/SparseFingerprint.cs src/Arius.Core.Tests/Shared/HashCache/SparseFingerprintTests.cs
git commit -m "feat(hashcache): deterministic sparse fingerprint (seek + sampler)"
```

---

### Task 3: HashCacheEntry + HashCacheLocalStore (SQLite)

**Files:**
- Create: `src/Arius.Core/Shared/HashCache/HashCacheEntry.cs`
- Create: `src/Arius.Core/Shared/HashCache/HashCacheLocalStore.cs`
- Test: `src/Arius.Core.Tests/Shared/HashCache/HashCacheLocalStoreTests.cs`

**Interfaces:**
- Produces:
  - `HashCacheEntry(RelativePath Path, long Size, long MtimeTicks, long? CtimeTicks, string? Inode, string? Dev, int SignalSet, byte[] SparseFingerprint, int FpAlgo, ContentHash ContentHash, long LastVerifiedTicks)`.
  - `HashCacheLocalStore(LocalDirectory root, ILogger<HashCacheLocalStore>? logger = null)`.
  - `HashCacheLocalStore.Find(RelativePath path) → HashCacheEntry?`.
  - `HashCacheLocalStore.Upsert(HashCacheEntry entry)`.
  - `HashCacheLocalStore.Delete(RelativePath path)`.
  - `HashCacheLocalStore.ConnectionString` (internal test seam).

Schema (`schema_version = 1`): `PRAGMA journal_mode = wal; PRAGMA synchronous = normal;`

```sql
CREATE TABLE IF NOT EXISTS metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS file_hashes (
    path           TEXT    NOT NULL PRIMARY KEY,
    size           INTEGER NOT NULL CHECK (size >= 0),
    mtime          INTEGER NOT NULL,
    ctime          INTEGER,
    inode          TEXT,
    dev            TEXT,
    signal_set     INTEGER NOT NULL,
    sparse_fp      BLOB    NOT NULL,
    fp_algo        INTEGER NOT NULL,
    content_hash   TEXT    NOT NULL,
    last_verified  INTEGER NOT NULL
);
```

- [ ] **Step 1: Write the failing tests**

```csharp
using Arius.Core.Shared;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.HashCache;
using Arius.Core.Shared.Hashes;
using Microsoft.Data.Sqlite;

namespace Arius.Core.Tests.Shared.HashCache;

public class HashCacheLocalStoreTests
{
    [Test]
    public void Initialize_CreatesSchemaVersionAndWalMode()
    {
        var store = NewStore(out var root);
        using var c = Open(root);
        using var v = c.CreateCommand();
        v.CommandText = "SELECT value FROM metadata WHERE key='schema_version';";
        v.ExecuteScalar().ShouldBe("1");
        using var j = c.CreateCommand();
        j.CommandText = "PRAGMA journal_mode;";
        j.ExecuteScalar().ShouldBe("wal");
    }

    [Test]
    public void Upsert_AndFind_RoundTrips()
    {
        var store = NewStore(out _);
        var entry = Sample();
        store.Upsert(entry);
        store.Find(entry.Path).ShouldBe(entry);
    }

    [Test]
    public void Upsert_SamePath_LastWriterWins()
    {
        var store = NewStore(out _);
        var first  = Sample();
        var second = first with { Size = 999, ContentHash = ContentHash.Parse(new string('b', 64)) };
        store.Upsert(first);
        store.Upsert(second);
        store.Find(first.Path)!.Value.Size.ShouldBe(999);
    }

    [Test]
    public void Find_MissingPath_ReturnsNull()
    {
        var store = NewStore(out _);
        store.Find(RelativePath.Parse("nope.bin")).ShouldBeNull();
    }

    [Test]
    public void Delete_RemovesRow()
    {
        var store = NewStore(out _);
        var entry = Sample();
        store.Upsert(entry);
        store.Delete(entry.Path);
        store.Find(entry.Path).ShouldBeNull();
    }

    private static HashCacheEntry Sample() => new(
        Path: RelativePath.Parse("dir/file.bin"),
        Size: 1234, MtimeTicks: 100, CtimeTicks: 200, Inode: "42", Dev: "dev-1",
        SignalSet: 1, SparseFingerprint: [1, 2, 3, 4], FpAlgo: SparseFingerprint.Algo,
        ContentHash: ContentHash.Parse(new string('a', 64)), LastVerifiedTicks: 300);

    private static HashCacheLocalStore NewStore(out LocalDirectory root)
    {
        var key = $"hc-{Guid.NewGuid():N}";
        root = RepositoryLocalStatePaths.GetHashCacheRoot(key, key);
        return new HashCacheLocalStore(root);
    }

    private static SqliteConnection Open(LocalDirectory root)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = root.Resolve(RelativePath.Root / PathSegment.Parse("cache.sqlite")),
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        c.Open();
        return c;
    }
}
```

- [ ] **Step 2: Run tests, verify they fail**

Run: `dotnet test --project src/Arius.Core.Tests --treenode-filter "/*/*/HashCacheLocalStoreTests/*"`
Expected: FAIL — types don't exist.

- [ ] **Step 3: Implement `HashCacheEntry`**

```csharp
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Shared.HashCache;

/// <summary>One persisted hashcache row: the cheap signals + sparse fingerprint + cached content hash for a path.</summary>
internal readonly record struct HashCacheEntry(
    RelativePath Path,
    long         Size,
    long         MtimeTicks,
    long?        CtimeTicks,
    string?      Inode,
    string?      Dev,
    int          SignalSet,
    byte[]       SparseFingerprint,
    int          FpAlgo,
    ContentHash  ContentHash,
    long         LastVerifiedTicks);
```

> `record struct` with a `byte[]` field uses reference equality for the array, which is fine for production. The `Upsert_AndFind_RoundTrips` test compares a re-read entry to the original; to make value-equality on the fingerprint bytes work in tests, compare fields explicitly. **Adjust that test** to assert field-by-field instead of `.ShouldBe(entry)`:
> ```csharp
> var found = store.Find(entry.Path)!.Value;
> found.Size.ShouldBe(entry.Size);
> found.CtimeTicks.ShouldBe(entry.CtimeTicks);
> found.Inode.ShouldBe(entry.Inode);
> found.SparseFingerprint.ShouldBe(entry.SparseFingerprint);
> found.ContentHash.ShouldBe(entry.ContentHash);
> found.SignalSet.ShouldBe(entry.SignalSet);
> ```

- [ ] **Step 4: Implement `HashCacheLocalStore`** (mirror `ChunkIndexLocalStore`'s connection/gate/schema idioms)

```csharp
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Hashes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arius.Core.Shared.HashCache;

/// <summary>
/// SQLite-backed local hashcache: maps a repository-relative path to the cheap change-signals,
/// sparse fingerprint, and cached content hash captured the last time the file was hashed.
/// A disposable accelerator — losing it costs a full-hash run, never data.
/// </summary>
internal sealed class HashCacheLocalStore
{
    private const string SchemaVersion = "1";

    private readonly RelativePath _databasePath = RelativePath.Root / PathSegment.Parse("cache.sqlite");
    private readonly LocalDirectory                _root;
    private readonly ILogger<HashCacheLocalStore>  _logger;
    private readonly string                        _connectionString;
    private readonly Lock                          _gate = new();

    public HashCacheLocalStore(LocalDirectory root, ILogger<HashCacheLocalStore>? logger = null)
    {
        _root   = root;
        _logger = logger ?? NullLogger<HashCacheLocalStore>.Instance;

        new RelativeFileSystem(root).CreateDirectory(RelativePath.Root);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = root.Resolve(_databasePath),
            Mode       = SqliteOpenMode.ReadWriteCreate,
            Pooling    = true,
        }.ToString();

        CreateOrUpgradeSchema();
    }

    internal string ConnectionString => _connectionString;

    public HashCacheEntry? Find(RelativePath path)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT size, mtime, ctime, inode, dev, signal_set, sparse_fp, fp_algo, content_hash, last_verified
            FROM file_hashes WHERE path = $path;
            """;
        command.Parameters.AddWithValue("$path", path.ToString());
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        return new HashCacheEntry(
            Path:              path,
            Size:              reader.GetInt64(0),
            MtimeTicks:        reader.GetInt64(1),
            CtimeTicks:        reader.IsDBNull(2) ? null : reader.GetInt64(2),
            Inode:             reader.IsDBNull(3) ? null : reader.GetString(3),
            Dev:               reader.IsDBNull(4) ? null : reader.GetString(4),
            SignalSet:         reader.GetInt32(5),
            SparseFingerprint: (byte[])reader[6],
            FpAlgo:            reader.GetInt32(7),
            ContentHash:       ContentHash.Parse(reader.GetString(8)),
            LastVerifiedTicks: reader.GetInt64(9));
    }

    public void Upsert(HashCacheEntry entry)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO file_hashes (path, size, mtime, ctime, inode, dev, signal_set, sparse_fp, fp_algo, content_hash, last_verified)
                VALUES ($path, $size, $mtime, $ctime, $inode, $dev, $signal_set, $sparse_fp, $fp_algo, $content_hash, $last_verified)
                ON CONFLICT(path) DO UPDATE SET
                    size=excluded.size, mtime=excluded.mtime, ctime=excluded.ctime, inode=excluded.inode,
                    dev=excluded.dev, signal_set=excluded.signal_set, sparse_fp=excluded.sparse_fp,
                    fp_algo=excluded.fp_algo, content_hash=excluded.content_hash, last_verified=excluded.last_verified;
                """;
            command.Parameters.AddWithValue("$path", entry.Path.ToString());
            command.Parameters.AddWithValue("$size", entry.Size);
            command.Parameters.AddWithValue("$mtime", entry.MtimeTicks);
            command.Parameters.AddWithValue("$ctime", (object?)entry.CtimeTicks ?? DBNull.Value);
            command.Parameters.AddWithValue("$inode", (object?)entry.Inode ?? DBNull.Value);
            command.Parameters.AddWithValue("$dev", (object?)entry.Dev ?? DBNull.Value);
            command.Parameters.AddWithValue("$signal_set", entry.SignalSet);
            command.Parameters.Add("$sparse_fp", SqliteType.Blob).Value = entry.SparseFingerprint;
            command.Parameters.AddWithValue("$fp_algo", entry.FpAlgo);
            command.Parameters.AddWithValue("$content_hash", entry.ContentHash.ToString());
            command.Parameters.AddWithValue("$last_verified", entry.LastVerifiedTicks);
            command.ExecuteNonQuery();
        }
    }

    public void Delete(RelativePath path)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM file_hashes WHERE path = $path;";
            command.Parameters.AddWithValue("$path", path.ToString());
            command.ExecuteNonQuery();
        }
    }

    private void CreateOrUpgradeSchema()
    {
        using var connection = OpenConnection();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode = wal; PRAGMA synchronous = normal;";
        pragma.ExecuteNonQuery();

        using var create = connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS metadata (
                key   TEXT NOT NULL PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS file_hashes (
                path           TEXT    NOT NULL PRIMARY KEY,
                size           INTEGER NOT NULL CHECK (size >= 0),
                mtime          INTEGER NOT NULL,
                ctime          INTEGER,
                inode          TEXT,
                dev            TEXT,
                signal_set     INTEGER NOT NULL,
                sparse_fp      BLOB    NOT NULL,
                fp_algo        INTEGER NOT NULL,
                content_hash   TEXT    NOT NULL,
                last_verified  INTEGER NOT NULL
            );
            """;
        create.ExecuteNonQuery();

        using var version = connection.CreateCommand();
        version.CommandText = "INSERT INTO metadata(key, value) VALUES ('schema_version', $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        version.Parameters.AddWithValue("$value", SchemaVersion);
        version.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
```

- [ ] **Step 5: Run tests, verify they pass; commit**

```bash
dotnet test --project src/Arius.Core.Tests --treenode-filter "/*/*/HashCacheLocalStoreTests/*"
git add src/Arius.Core/Shared/HashCache/HashCacheEntry.cs src/Arius.Core/Shared/HashCache/HashCacheLocalStore.cs src/Arius.Core.Tests/Shared/HashCache/HashCacheLocalStoreTests.cs
git commit -m "feat(hashcache): SQLite local store (upsert/find/delete)"
```

---

### Task 4: `FileChangeSignals` + `RelativeFileSystem.TryGetChangeSignals` (native interop)

> **Highest-risk task.** Native `stat`/file-info layouts vary by OS/arch. Implement behind one static API and a value type; the verdict ladder already treats `null` as "use the floor", so a platform where signals are unavailable is correct, just without the zero-read lane. **Before locking constants/magics, run Task 13's benchmark on the actual Synology image.**

**Files:**
- Create: `src/Arius.Core/Shared/HashCache/FileChangeSignals.cs`
- Create: `src/Arius.Core/Shared/FileSystem/NativeFileSignals.cs`
- Modify: `src/Arius.Core/Shared/FileSystem/RelativeFileSystem.cs`
- Modify: `Directory.Packages.props` (+ `src/Arius.Core/Arius.Core.csproj`) — add `Mono.Posix.NETStandard`
- Test: `src/Arius.Core.Tests/Shared/FileSystem/TryGetChangeSignalsTests.cs`

**Interfaces:**
- Produces:
  - `FileChangeSignals(long CtimeTicks, string Inode, string Dev, int SignalSet)`.
  - `SignalSets` (`static class`): `const int None = 0; const int Posix = 1; const int Windows = 2;`
  - `RelativeFileSystem.TryGetChangeSignals(RelativePath path) → FileChangeSignals?` — `null` on network/unsupported FS or any failure.

- [ ] **Step 1: Add the POSIX package**

Run (per package-management skill — do not hand-edit XML):
```bash
dotnet add src/Arius.Core/Arius.Core.csproj package Mono.Posix.NETStandard
```
Verify it landed in `Directory.Packages.props` (CPM) and `Arius.Core.csproj` references it without a version.

- [ ] **Step 2: Write the failing test** (behavioural: on a normal local temp file, native FS yields signals that change after a content write; same path read twice is stable)

```csharp
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.HashCache;

namespace Arius.Core.Tests.Shared.FileSystem;

public class TryGetChangeSignalsTests
{
    [Test]
    public void LocalFile_OnNativeFilesystem_YieldsStableSignals()
    {
        var (fs, path) = WriteTemp([1, 2, 3]);
        var a = fs.TryGetChangeSignals(path);
        var b = fs.TryGetChangeSignals(path);

        // On the CI runner's native FS we expect signals; assert stability + shape when present.
        if (a is null) return; // unsupported FS on this runner → floor path is exercised elsewhere
        b.ShouldNotBeNull();
        a.Value.Inode.ShouldBe(b!.Value.Inode);
        a.Value.Dev.ShouldBe(b.Value.Dev);
        a.Value.CtimeTicks.ShouldBe(b.Value.CtimeTicks);
        a.Value.SignalSet.ShouldBeOneOf(SignalSets.Posix, SignalSets.Windows);
    }

    [Test]
    public void Rewriting_Content_MovesCtime()
    {
        var (fs, path) = WriteTemp([1, 2, 3]);
        var before = fs.TryGetChangeSignals(path);
        if (before is null) return;

        Thread.Sleep(20);
        fs.WriteAllBytes(path, [4, 5, 6, 7]);
        var after = fs.TryGetChangeSignals(path);
        after!.Value.CtimeTicks.ShouldBeGreaterThan(before.Value.CtimeTicks);
    }

    private static (RelativeFileSystem, RelativePath) WriteTemp(byte[] data)
    {
        var dir  = LocalDirectory.Parse(Path.Combine(Path.GetTempPath(), $"arius-sig-{Guid.NewGuid():N}"));
        var fs   = new RelativeFileSystem(dir);
        var path = RelativePath.Parse("f.bin");
        fs.WriteAllBytes(path, data);
        return (fs, path);
    }
}
```

- [ ] **Step 3: Run test, verify it fails**

Run: `dotnet test --project src/Arius.Core.Tests --treenode-filter "/*/*/TryGetChangeSignalsTests/*"`
Expected: FAIL — `TryGetChangeSignals` / `FileChangeSignals` don't exist.

- [ ] **Step 4: Implement `FileChangeSignals`**

```csharp
namespace Arius.Core.Shared.HashCache;

/// <summary>Cheap, platform-provided change signals for one file. See <see cref="SignalSets"/>.</summary>
internal readonly record struct FileChangeSignals(long CtimeTicks, string Inode, string Dev, int SignalSet);

/// <summary>Provenance tag stored on a hashcache row so signals are only compared within the same source.</summary>
internal static class SignalSets
{
    public const int None    = 0;
    public const int Posix   = 1;
    public const int Windows = 2;
}
```

- [ ] **Step 5: Implement `NativeFileSignals`** (the platform interop; returns `null` on network/unsupported FS or any error)

```csharp
using System.Runtime.InteropServices;
using Arius.Core.Shared.HashCache;
using Mono.Unix.Native;

namespace Arius.Core.Shared.FileSystem;

/// <summary>
/// Platform-specific capture of (ctime, inode, dev). Returns <c>null</c> on network filesystems
/// (SMB/CIFS/NFS) and anywhere the signals can't be trusted, so the caller falls back to the
/// sparse-fingerprint floor. See the spec's "Platform signal mapping".
/// </summary>
internal static class NativeFileSignals
{
    public static FileChangeSignals? TryGet(string fullPath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return TryGetWindows(fullPath);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                return TryGetPosix(fullPath);
            return null;
        }
        catch
        {
            return null; // never fault the archive pipeline over a signals probe
        }
    }

    // ---- POSIX (Linux/macOS) via Mono.Posix ---------------------------------

    private static FileChangeSignals? TryGetPosix(string fullPath)
    {
        if (IsPosixNetworkFs(fullPath))
            return null;

        if (Syscall.stat(fullPath, out var st) != 0)
            return null;

        // st_ctime is whole seconds; add nanoseconds when the platform exposes them.
        var ctimeTicks = DateTimeOffset.FromUnixTimeSeconds(st.st_ctime).UtcTicks
                         + st.st_ctime_nsec / 100; // 100 ns per tick
        return new FileChangeSignals(
            CtimeTicks: ctimeTicks,
            Inode:      st.st_ino.ToString(),
            Dev:        st.st_dev.ToString(),
            SignalSet:  SignalSets.Posix);
    }

    private static bool IsPosixNetworkFs(string fullPath)
    {
        // Linux: statfs.f_type magic. macOS: statfs has no portable magic here → trust local
        // (network detection on macOS is best-effort; the target is Synology/Windows).
        if (!OperatingSystem.IsLinux())
            return false;

        if (Syscall.statvfs(fullPath, out _) != 0)
            return true; // can't classify → be conservative, use the floor

        // Mono.Posix exposes statfs on Linux; magic numbers for network filesystems:
        if (Syscall.statfs(fullPath, out var fs) != 0)
            return true;
        const long CIFS = 0xFF534D42, SMB2 = unchecked((long)0xFE534D42), NFS = 0x6969, SMB = 0x517B;
        var t = (long)fs.f_type;
        return t is CIFS or SMB2 or NFS or SMB;
    }

    // ---- Windows via GetFileInformationByHandleEx ---------------------------

    private static FileChangeSignals? TryGetWindows(string fullPath)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(fullPath));
        if (root is not null && GetDriveType(root) == DRIVE_REMOTE)
            return null;

        using var handle = File.OpenHandle(fullPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var h = handle.DangerousGetHandle();

        if (!GetFileInformationByHandleEx(h, FileBasicInfo, out FILE_BASIC_INFO basic, Marshal.SizeOf<FILE_BASIC_INFO>()))
            return null;
        if (!GetFileInformationByHandleEx(h, FileIdInfo, out FILE_ID_INFO id, Marshal.SizeOf<FILE_ID_INFO>()))
            return null;

        // ChangeTime is a FILETIME (100 ns ticks since 1601) → UTC ticks.
        var ctimeTicks = DateTime.FromFileTimeUtc(basic.ChangeTime).Ticks;
        return new FileChangeSignals(
            CtimeTicks: ctimeTicks,
            Inode:      Convert.ToHexString(id.FileId),                 // 128-bit FileId
            Dev:        id.VolumeSerialNumber.ToString(),
            SignalSet:  SignalSets.Windows);
    }

    private const int  DRIVE_REMOTE = 4;
    private const int  FileBasicInfo = 0;
    private const int  FileIdInfo = 18;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetDriveType(string lpRootPathName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(IntPtr hFile, int infoClass, out FILE_BASIC_INFO info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(IntPtr hFile, int infoClass, out FILE_ID_INFO info, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_BASIC_INFO
    {
        public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ID_INFO
    {
        public ulong VolumeSerialNumber;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] FileId;
    }
}
```

> If `Syscall.statfs`/`Statfs.f_type` is unavailable in the chosen Mono.Posix version, make `IsPosixNetworkFs` return `false` (trust local) and rely on the Windows `DRIVE_REMOTE` check plus the documented "network FS is not a target" stance; record the limitation in the ADR. Confirm the exact `Stat` field names (`st_ctime`, `st_ctime_nsec`, `st_ino`, `st_dev`) against the installed Mono.Posix during implementation.

- [ ] **Step 6: Wire `TryGetChangeSignals` into `RelativeFileSystem`**

Add to `RelativeFileSystem.cs` (near `GetTimestamps`):

```csharp
    /// <summary>
    /// Returns platform change-signals (ctime, inode, dev) for the file, or <c>null</c> on network/
    /// unsupported filesystems or any failure — in which case fast-hash uses the sparse-fingerprint floor.
    /// </summary>
    public FileChangeSignals? TryGetChangeSignals(RelativePath path)
        => NativeFileSignals.TryGet(root.Resolve(path));
```

Add `using Arius.Core.Shared.HashCache;` to `RelativeFileSystem.cs` if not already imported.

- [ ] **Step 7: Run tests on this platform, verify pass; build all TFMs locally available**

Run: `dotnet test --project src/Arius.Core.Tests --treenode-filter "/*/*/TryGetChangeSignalsTests/*"`
Expected: PASS on the dev machine's native FS. (On unsupported FS the tests early-return — acceptable.)

- [ ] **Step 8: Commit**

```bash
git add Directory.Packages.props src/Arius.Core/Arius.Core.csproj src/Arius.Core/Shared/HashCache/FileChangeSignals.cs src/Arius.Core/Shared/FileSystem/NativeFileSignals.cs src/Arius.Core/Shared/FileSystem/RelativeFileSystem.cs src/Arius.Core.Tests/Shared/FileSystem/TryGetChangeSignalsTests.cs
git commit -m "feat(hashcache): platform change-signals (ctime/inode/dev) with network-FS fallback"
```

---

### Task 5: `HashCacheService` — the verdict ladder

**Files:**
- Create: `src/Arius.Core/Shared/HashCache/FastHashResult.cs`
- Create: `src/Arius.Core/Shared/HashCache/IHashCacheService.cs`
- Create: `src/Arius.Core/Shared/HashCache/HashCacheService.cs`
- Test: `src/Arius.Core.Tests/Shared/HashCache/HashCacheServiceTests.cs`

**Interfaces:**
- Consumes: `HashCacheLocalStore` (Task 3), `SparseFingerprint` (Task 2), `RelativeFileSystem.TryGetChangeSignals` (Task 4), `IEncryptionService`-independent (no hashing here).
- Produces:
  - `FastHashResult(ContentHash? Hash, string Reason)` with `bool IsHit => Hash is not null`.
  - `IHashCacheService.TryReuse(RelativeFileSystem fs, RelativePath path, long liveSize, long nowTicks) → FastHashResult` — the ladder; on a hit it refreshes the row (`last_verified`, moved ctime). Reasons: `"ctime match"`, `"size+fp match"`, `"cache miss"`, `"fp_algo bump"`, `"size 100->120"`, `"fp differs"`.
  - `IHashCacheService.Record(RelativePath path, long size, FileChangeSignals? signals, byte[] sparseFp, ContentHash hash, long nowTicks)` — upsert after a full hash.

> `nowTicks` is passed in (caller supplies `DateTimeOffset.UtcNow.UtcTicks`) so the service stays clock-free and unit-testable.

- [ ] **Step 1: Write the failing tests**

```csharp
using Arius.Core.Shared;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.HashCache;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Tests.Shared.HashCache;

public class HashCacheServiceTests
{
    [Test]
    public void Miss_WhenNoRow_ReturnsMiss()
    {
        var (svc, fs, path) = Setup([1, 2, 3, 4]);
        var r = svc.TryReuse(fs, path, fs.GetFileSize(path), now: 10);
        r.IsHit.ShouldBeFalse();
        r.Reason.ShouldBe("cache miss");
    }

    [Test]
    public void Hit_WhenCtimeMatches_SkipsReads()
    {
        var (svc, fs, path) = Setup([1, 2, 3, 4]);
        var hash = ContentHash.Parse(new string('a', 64));
        var sig  = fs.TryGetChangeSignals(path);
        if (sig is null) return; // floor-only platform; covered by Hit_WhenFpMatches
        svc.Record(path, fs.GetFileSize(path), sig, [9, 9], hash, now: 1); // store bogus fp on purpose

        var r = svc.TryReuse(fs, path, fs.GetFileSize(path), now: 2);
        r.Hash.ShouldBe(hash);             // reused without consulting the (bogus) fp
        r.Reason.ShouldBe("ctime match");
    }

    [Test]
    public void Hit_WhenFpMatches_AfterCtimeMoved()
    {
        var (svc, fs, path) = Setup([1, 2, 3, 4]);
        var hash = ContentHash.Parse(new string('a', 64));
        var realFp = SparseFingerprint.ComputeBySeeking(fs, path, fs.GetFileSize(path));
        // Store a row whose ctime/inode won't match the live file (force the floor branch).
        svc.Record(path, fs.GetFileSize(path), signals: null, realFp, hash, now: 1);

        var r = svc.TryReuse(fs, path, fs.GetFileSize(path), now: 2);
        r.Hash.ShouldBe(hash);
        r.Reason.ShouldBe("size+fp match");
    }

    [Test]
    public void Miss_WhenSizeChanged()
    {
        var (svc, fs, path) = Setup([1, 2, 3, 4]);
        svc.Record(path, size: 999, signals: null, [1], ContentHash.Parse(new string('a', 64)), now: 1);
        var r = svc.TryReuse(fs, path, fs.GetFileSize(path), now: 2);
        r.IsHit.ShouldBeFalse();
        r.Reason.ShouldStartWith("size ");
    }

    [Test]
    public void Miss_WhenFpAlgoBumped()
    {
        var (svc, fs, path) = Setup([1, 2, 3, 4]);
        var fp = SparseFingerprint.ComputeBySeeking(fs, path, fs.GetFileSize(path));
        // Persist a row with a stale fp_algo via the store directly.
        var store = NewStore(out var root, fs);
        store.Upsert(new HashCacheEntry(path, fs.GetFileSize(path), 0, null, null, null,
            SignalSets.None, fp, FpAlgo: 999, ContentHash.Parse(new string('a', 64)), 1));
        var svc2 = new HashCacheService(store);
        svc2.TryReuse(fs, path, fs.GetFileSize(path), now: 2).Reason.ShouldBe("fp_algo bump");
    }

    [Test]
    public void Miss_WhenContentChangedInSampledRegion()
    {
        var (svc, fs, path) = Setup(Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray());
        var fp = SparseFingerprint.ComputeBySeeking(fs, path, fs.GetFileSize(path));
        svc.Record(path, fs.GetFileSize(path), signals: null, fp, ContentHash.Parse(new string('a', 64)), now: 1);

        var bytes = fs.ReadAllBytes(path); bytes[0] ^= 0xFF; fs.WriteAllBytes(path, bytes); // same size
        svc.TryReuse(fs, path, fs.GetFileSize(path), now: 2).Reason.ShouldBe("fp differs");
    }

    private static (HashCacheService, RelativeFileSystem, RelativePath) Setup(byte[] data)
    {
        var dir  = LocalDirectory.Parse(Path.Combine(Path.GetTempPath(), $"arius-hcs-{Guid.NewGuid():N}"));
        var fs   = new RelativeFileSystem(dir);
        var path = RelativePath.Parse("f.bin");
        fs.WriteAllBytes(path, data);
        var store = NewStore(out _, fs);
        return (new HashCacheService(store), fs, path);
    }

    private static HashCacheLocalStore NewStore(out LocalDirectory root, RelativeFileSystem _)
    {
        var key = $"hcs-{Guid.NewGuid():N}";
        root = RepositoryLocalStatePaths.GetHashCacheRoot(key, key);
        return new HashCacheLocalStore(root);
    }
}
```

- [ ] **Step 2: Run tests, verify they fail**

Run: `dotnet test --project src/Arius.Core.Tests --treenode-filter "/*/*/HashCacheServiceTests/*"`
Expected: FAIL — service/types don't exist.

- [ ] **Step 3: Implement `FastHashResult` + `IHashCacheService`**

```csharp
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Shared.HashCache;

internal readonly record struct FastHashResult(ContentHash? Hash, string Reason)
{
    public bool IsHit => Hash is not null;
    public static FastHashResult Hit(ContentHash hash, string reason) => new(hash, reason);
    public static FastHashResult Miss(string reason) => new(null, reason);
}
```

```csharp
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Shared.HashCache;

internal interface IHashCacheService
{
    FastHashResult TryReuse(RelativeFileSystem fs, RelativePath path, long liveSize, long now);
    void Record(RelativePath path, long size, FileChangeSignals? signals, byte[] sparseFingerprint, ContentHash hash, long now);
}
```

- [ ] **Step 4: Implement `HashCacheService`**

```csharp
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Shared.HashCache;

/// <summary>
/// Per-repository fast-hash facade: the verdict ladder over <see cref="HashCacheLocalStore"/>.
/// Validates against the live file (never a pointer); a miss means the caller must full-hash.
/// </summary>
internal sealed class HashCacheService : IHashCacheService
{
    private readonly HashCacheLocalStore _store;

    public HashCacheService(HashCacheLocalStore store) => _store = store;

    public FastHashResult TryReuse(RelativeFileSystem fs, RelativePath path, long liveSize, long now)
    {
        var row = _store.Find(path);
        if (row is null)
            return FastHashResult.Miss("cache miss");
        var e = row.Value;

        if (e.FpAlgo != SparseFingerprint.Algo)
            return FastHashResult.Miss("fp_algo bump");

        if (liveSize != e.Size)
            return FastHashResult.Miss($"size {e.Size}->{liveSize}");

        // ctime fast-lane: same file (dev+inode) and untouched (ctime) → reuse with no reads.
        var sig = fs.TryGetChangeSignals(path);
        if (sig is { } s
            && s.SignalSet == e.SignalSet
            && e.Inode is not null && e.Dev is not null && e.CtimeTicks is not null
            && s.Inode == e.Inode && s.Dev == e.Dev && s.CtimeTicks == e.CtimeTicks)
        {
            _store.Upsert(e with { LastVerifiedTicks = now });
            return FastHashResult.Hit(e.ContentHash, "ctime match");
        }

        // Floor: sample bytes and compare the fingerprint.
        var liveFp = SparseFingerprint.ComputeBySeeking(fs, path, liveSize);
        if (liveFp.AsSpan().SequenceEqual(e.SparseFingerprint))
        {
            _store.Upsert(e with
            {
                CtimeTicks        = sig?.CtimeTicks,
                Inode             = sig?.Inode,
                Dev               = sig?.Dev,
                SignalSet         = sig?.SignalSet ?? SignalSets.None,
                LastVerifiedTicks = now,
            });
            return FastHashResult.Hit(e.ContentHash, "size+fp match");
        }

        return FastHashResult.Miss("fp differs");
    }

    public void Record(RelativePath path, long size, FileChangeSignals? signals, byte[] sparseFingerprint, ContentHash hash, long now)
    {
        _store.Upsert(new HashCacheEntry(
            Path: path, Size: size,
            MtimeTicks: now, // mtime stored for diagnostics only; not in the verdict
            CtimeTicks: signals?.CtimeTicks,
            Inode:      signals?.Inode,
            Dev:        signals?.Dev,
            SignalSet:  signals?.SignalSet ?? SignalSets.None,
            SparseFingerprint: sparseFingerprint,
            FpAlgo:     SparseFingerprint.Algo,
            ContentHash: hash,
            LastVerifiedTicks: now));
    }
}
```

> `MtimeTicks` is populated with `now` here for simplicity — it is diagnostics-only and never read by the verdict. If you want true mtime, pass `fs.GetTimestamps(path).Modified.UtcTicks` from the caller; not required for v1.

- [ ] **Step 5: Run tests, verify pass; commit**

```bash
dotnet test --project src/Arius.Core.Tests --treenode-filter "/*/*/HashCacheServiceTests/*"
git add src/Arius.Core/Shared/HashCache/FastHashResult.cs src/Arius.Core/Shared/HashCache/IHashCacheService.cs src/Arius.Core/Shared/HashCache/HashCacheService.cs src/Arius.Core.Tests/Shared/HashCache/HashCacheServiceTests.cs
git commit -m "feat(hashcache): verdict ladder service (ctime fast-lane + fp floor)"
```

---

### Task 6: `ArchiveCommandOptions.FastHash`

**Files:**
- Modify: `src/Arius.Core/Features/ArchiveCommand/ArchiveCommand.cs`
- Test: covered by Task 7's handler test.

- [ ] **Step 1: Add the option**

In `ArchiveCommandOptions`, after `NoPointers`:

```csharp
    /// <summary>If <c>true</c>, skip re-reading a binary whose content the hashcache verifies as unchanged.</summary>
    public bool FastHash { get; init; } = false;
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Arius.Core`
Expected: success.

- [ ] **Step 3: Commit**

```bash
git add src/Arius.Core/Features/ArchiveCommand/ArchiveCommand.cs
git commit -m "feat(archive): add FastHash option (default off)"
```

---

### Task 7: Stage-2 integration + logging

**Files:**
- Modify: `src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs`
- Create: `src/Arius.Core/Shared/HashCache/SparseSamplingStream.cs` (read-through tee)
- Test: `src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveFastHashTests.cs`

**Interfaces:**
- Consumes: `IHashCacheService` (Task 5), `SparseFingerprint.Sampler` (Task 2), `RelativeFileSystem.TryGetChangeSignals` (Task 4).
- Produces: handler now takes an `IHashCacheService` constructor arg (used by Task 9 DI).

> Constructor change: add `IHashCacheService hashCache` as a parameter to **both** `ArchiveCommandHandler` constructors and store it in a `_hashCache` field. Update the delegating `: this(...)` call accordingly.

- [ ] **Step 1: Implement `SparseSamplingStream`** (wraps the read stream; tees sampled regions; exposes the fingerprint)

```csharp
namespace Arius.Core.Shared.HashCache;

/// <summary>
/// Read-only pass-through stream that feeds every byte it serves into a <see cref="SparseFingerprint.Sampler"/>,
/// so the sparse fingerprint is produced for free while the file is fully hashed for upload.
/// </summary>
internal sealed class SparseSamplingStream : Stream
{
    private readonly Stream                    _inner;
    private readonly SparseFingerprint.Sampler _sampler;
    private long                               _position;

    public SparseSamplingStream(Stream inner, long size)
    {
        _inner   = inner;
        _sampler = new SparseFingerprint.Sampler(size);
    }

    public byte[] Fingerprint() => _sampler.Finish();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = _inner.Read(buffer, offset, count);
        if (n > 0) { _sampler.Capture(_position, buffer.AsSpan(offset, n)); _position += n; }
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await _inner.ReadAsync(buffer, cancellationToken);
        if (n > 0) { _sampler.Capture(_position, buffer.Span[..n]); _position += n; }
        return n;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
}
```

- [ ] **Step 2: Write the failing handler test** (warm cache + `FastHash` ⇒ unchanged file is not re-read; cold ⇒ behaves as today)

> Use the existing archive test harness/fixture in `src/Arius.Core.Tests/Features/ArchiveCommand/` (mirror an existing handler test's setup — Azurite or in-memory blob double, `FakeLogger<T>`). The new assertions:
> 1. Archive a directory with `FastHash = true`; assert success.
> 2. Without changing files, archive again with `FastHash = true`; assert the run deduped everything and (via a `FakeLogger<ArchiveCommandHandler>` scan) emitted `[fast-hash] … → reused` lines and a summary with `rehashed 0`.
> 3. Delete the hashcache dir (`RepositoryLocalStatePaths.GetHashCacheRoot(account, container)`); archive again with `FastHash = true`; assert it full-hashed (no "reused" lines).

```csharp
// Skeleton — adapt to the existing archive fixture in this folder.
[Test]
public async Task SecondRun_WithFastHash_ReusesHashes_NoRehash()
{
    await using var ctx = await ArchiveTestContext.CreateAsync();        // existing fixture
    ctx.WriteFile("a.bin", RandomBytes(2_000_000));

    (await ctx.ArchiveAsync(fastHash: true)).Success.ShouldBeTrue();

    ctx.Logger.Clear();
    var second = await ctx.ArchiveAsync(fastHash: true);
    second.Success.ShouldBeTrue();
    ctx.Logger.LatestRecords.ShouldContain(r => r.Message.Contains("[fast-hash]") && r.Message.Contains("reused"));
    ctx.Logger.LatestRecords.ShouldContain(r => r.Message.Contains("rehashed 0"));
}
```

- [ ] **Step 3: Run test, verify it fails**

Run: `dotnet test --project src/Arius.Core.Tests --treenode-filter "/*/*/ArchiveFastHashTests/*"`
Expected: FAIL — handler doesn't consult the cache / no `fast-hash` log lines.

- [ ] **Step 4: Integrate into Stage 2.** Replace the binary-hashing branch in `ArchiveCommandHandler.Handle` (the `else if (pair.Binary is not null)` block around `ArchiveCommandHandler.cs:340-346`) with:

```csharp
                                else if (pair.Binary is not null)
                                {
                                    var now = DateTimeOffset.UtcNow.UtcTicks;

                                    if (opts.FastHash)
                                    {
                                        var verdict = _hashCache.TryReuse(fs, pair.RelativePath, fileSize, now);
                                        if (verdict.IsHit)
                                        {
                                            contentHash = verdict.Hash!.Value;
                                            Interlocked.Increment(ref fastHashReused);
                                            _logger.LogDebug("[fast-hash] {Path} -> reused ({Reason})", pair.RelativePath, verdict.Reason);
                                            goto hashed; // skip the full read
                                        }
                                        _logger.LogDebug("[fast-hash] {Path} -> full-hash ({Reason})", pair.RelativePath, verdict.Reason);
                                    }

                                    await using var s   = fs.OpenRead(pair.RelativePath);
                                    var             p   = opts.CreateHashProgress?.Invoke(pair.RelativePath, fileSize) ?? new Progress<long>();
                                    await using var smp = new SparseSamplingStream(s, fileSize);
                                    await using var ps  = new ProgressStream(smp, p);
                                    contentHash = await _encryption.ComputeHashAsync(ps, ct);

                                    // Populate the cache (free fingerprint from the read-through; signals captured cheaply).
                                    var signals = fs.TryGetChangeSignals(pair.RelativePath);
                                    _hashCache.Record(pair.RelativePath, fileSize, signals, smp.Fingerprint(), contentHash, now);
                                    Interlocked.Increment(ref fastHashRehashed);
                                }
```

Add a label + the existing post-hash code path. Concretely, after the `if/else if/else` block that assigns `contentHash`, add the `hashed:` label immediately before the line `await _mediator.Publish(new FileHashedEvent(...))`:

```csharp
                                hashed:
                                await _mediator.Publish(new FileHashedEvent(pair.RelativePath, contentHash), ct);
```

> `goto` into shared post-processing keeps the reuse path from duplicating the event/timestamp/emit code. If you prefer no `goto`, extract the assignment into a local `async` function returning `ContentHash` and branch on the verdict — either is acceptable; match the reviewer's taste.

Add the counters near the other `long` counters at the top of `Handle`:

```csharp
        var fastHashReused   = 0L;
        var fastHashRehashed = 0L;
```

Add the field + constructor wiring: declare `private readonly IHashCacheService _hashCache;`, add `IHashCacheService hashCache` to both constructors, assign `_hashCache = hashCache;`, and forward it in the delegating `: this(...)` call.

- [ ] **Step 5: Emit the per-run summary.** After the pipeline completes (near where `[archive] Start` was logged, at end of `Handle` before building the result), add:

```csharp
        if (opts.FastHash)
            _logger.LogInformation("[fast-hash] summary: reused {Reused}, rehashed {Rehashed}", fastHashReused, fastHashRehashed);
```

- [ ] **Step 6: Run test, verify pass**

Run: `dotnet test --project src/Arius.Core.Tests --treenode-filter "/*/*/ArchiveFastHashTests/*"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs src/Arius.Core/Shared/HashCache/SparseSamplingStream.cs src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveFastHashTests.cs
git commit -m "feat(archive): consult hashcache under --fast-hash; populate it on full hash"
```

---

### Task 8: DI registration

**Files:**
- Modify: `src/Arius.Core/ServiceCollectionExtensions.cs`
- Test: `src/Arius.Core.Tests` build + an existing archive integration test still passes.

- [ ] **Step 1: Register the service** (in `AddArius`, beside the other `AddSingleton` factories)

```csharp
        services.AddSingleton<IHashCacheService>(sp =>
            new HashCacheService(
                new HashCacheLocalStore(
                    RepositoryLocalStatePaths.GetHashCacheRoot(accountName, containerName),
                    sp.GetRequiredService<ILogger<HashCacheLocalStore>>())));
```

Add `using Arius.Core.Shared.HashCache;`.

- [ ] **Step 2: Pass it to the handler.** In the `ICommandHandler<ArchiveCommand, ArchiveResult>` factory, add `sp.GetRequiredService<IHashCacheService>(),` in the argument position matching Task 7's constructor signature.

- [ ] **Step 3: Build + run an existing archive integration test**

Run: `dotnet build src/Arius.Core && dotnet test --project src/Arius.Core.Tests --treenode-filter "/*/*/ArchiveFastHashTests/*"`
Expected: PASS (handler now resolvable through DI).

- [ ] **Step 4: Commit**

```bash
git add src/Arius.Core/ServiceCollectionExtensions.cs
git commit -m "feat(di): register IHashCacheService per repository"
```

---

### Task 9: CLI `--fast-hash`

**Files:**
- Modify: `src/Arius.Cli/Commands/Archive/ArchiveVerb.cs`
- Test: `src/Arius.Cli.Tests` (mirror an existing `ArchiveVerb` option test if present; otherwise a parse test).

- [ ] **Step 1: Add the option + wire it.** After `noPointersOption`:

```csharp
        var fastHashOption = new Option<bool>("--fast-hash")
        {
            Description = "Skip re-reading files the local hashcache verifies as unchanged",
        };
```

Register it: `cmd.Options.Add(fastHashOption);`
Read it: `var fastHash = parseResult.GetValue(fastHashOption);`
Set it on options: add `FastHash = fastHash,` to the `new ArchiveCommandOptions { ... }`.

- [ ] **Step 2: Build + smoke test**

Run: `dotnet build src/Arius.Cli && dotnet run --project src/Arius.Cli -- archive --help`
Expected: `--fast-hash` appears in help output.

- [ ] **Step 3: Commit**

```bash
git add src/Arius.Cli/Commands/Archive/ArchiveVerb.cs
git commit -m "feat(cli): add --fast-hash to archive"
```

---

# Phase 2 — pointer default flip + hosts

### Task 10: Flip pointer default in Core (`WritePointers`)

**Files:**
- Modify: `src/Arius.Core/Features/ArchiveCommand/ArchiveCommand.cs`
- Modify: `src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs`
- Test: `src/Arius.Core.Tests/Features/ArchiveCommand/` — pointer-default tests.

**Interfaces:**
- Produces: `ArchiveCommandOptions.WritePointers` (default `false`) replacing `NoPointers`.

- [ ] **Step 1: Write failing tests** — (a) default run writes **no** pointer for a binary-present file; (b) `WritePointers = true` writes one; (c) `RemoveLocal = true` writes a pointer even though `WritePointers` defaults false; (d) a pre-existing pointer-only (thin) entry is preserved/upgraded regardless.

```csharp
[Test]
public async Task Default_DoesNotWritePointers_ForBinaryPresentFiles()
{
    await using var ctx = await ArchiveTestContext.CreateAsync();
    ctx.WriteFile("a.bin", RandomBytes(1000));
    (await ctx.ArchiveAsync(writePointers: false, removeLocal: false)).Success.ShouldBeTrue();
    ctx.PointerExists("a.bin").ShouldBeFalse();
    ctx.BinaryExists("a.bin").ShouldBeTrue();
}

[Test]
public async Task RemoveLocal_WritesPointer_EvenWhenWritePointersFalse()
{
    await using var ctx = await ArchiveTestContext.CreateAsync();
    ctx.WriteFile("a.bin", RandomBytes(1000));
    (await ctx.ArchiveAsync(writePointers: false, removeLocal: true)).Success.ShouldBeTrue();
    ctx.PointerExists("a.bin").ShouldBeTrue();
    ctx.BinaryExists("a.bin").ShouldBeFalse();
}
```

- [ ] **Step 2: Run, verify fail.** `dotnet test --project src/Arius.Core.Tests --treenode-filter "/*/*/ArchivePointerDefaultTests/*"` → FAIL.

- [ ] **Step 3: Rename the option.** In `ArchiveCommand.cs` replace:

```csharp
    /// <summary>If <c>true</c>, do not create or update <c>.pointer.arius</c> files.</summary>
    public bool NoPointers { get; init; } = false;
```

with:

```csharp
    /// <summary>If <c>true</c>, create/update <c>.pointer.arius</c> sidecars for binary-present files. Default off.</summary>
    public bool WritePointers { get; init; } = false;
```

- [ ] **Step 4: Update the handler.** In `ArchiveCommandHandler.cs`:
  - Remove the `RemoveLocal && NoPointers` mutual-exclusion validation block (`ArchiveCommandHandler.cs:193-206`).
  - Compute an effective flag once: `var writePointers = opts.WritePointers || opts.RemoveLocal;`
  - Replace the two `!opts.NoPointers` predicates (`:624` and `:693`) with `writePointers`. The pointer-only legacy-upgrade clause stays: a `pair.Pointer?.IsLegacyFormat == true` file is still rewritten (sole local record), i.e.:

```csharp
                    if ((writePointers && pair.Binary is not null) || (pair.Pointer?.IsLegacyFormat ?? false))
                        pendingPointers.Add(new PendingPointerWrite(...));
```

  - Update the `_logger.LogInformation("[archive] Start: …")` line to log `writePointers={WritePointers}` instead of `noPointers`.

- [ ] **Step 5: Run tests, verify pass.** Same filter → PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Arius.Core/Features/ArchiveCommand/ArchiveCommand.cs src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs src/Arius.Core.Tests/Features/ArchiveCommand/ArchivePointerDefaultTests.cs
git commit -m "feat(archive): pointers off by default; --remove-local implies write-pointers"
```

---

### Task 11: CLI flag swap (`--write-pointers`)

**Files:**
- Modify: `src/Arius.Cli/Commands/Archive/ArchiveVerb.cs`
- Test: `src/Arius.Cli.Tests`.

- [ ] **Step 1:** Replace `noPointersOption` with:

```csharp
        var writePointersOption = new Option<bool>("--write-pointers")
        {
            Description = "Write .pointer.arius sidecars (default: off)",
        };
```

Update `cmd.Options.Add(...)`, the `parseResult.GetValue(...)`, remove the `removeLocal && noPointers` error block, and set `WritePointers = writePointers,` on the options.

- [ ] **Step 2:** Build + `dotnet run --project src/Arius.Cli -- archive --help` → shows `--write-pointers`, no `--no-pointers`.

- [ ] **Step 3: Commit**

```bash
git add src/Arius.Cli/Commands/Archive/ArchiveVerb.cs
git commit -m "feat(cli): replace --no-pointers with --write-pointers (default off)"
```

---

### Task 12: Web archive pane + Api default

**Files:**
- Modify: `src/Arius.Web/src/app/core/state/drawer.store.ts`
- Modify: `src/Arius.Web/src/app/features/drawer/archive-restore-drawer.component.ts`
- Modify: `src/Arius.Web/e2e/specs/archive.spec.ts`
- Modify: `src/Arius.Api/Jobs/JobRunner.cs`

- [ ] **Step 1: Store.** In `drawer.store.ts` replace the `removeLocal`/`noPointers` toggles with a single `archiveOnDisk` signal (`'keep' | 'keep-pointers' | 'replace'`, default `'keep'`) and add `fastHash` (default `false`). Map to the API request: `removeLocal = archiveOnDisk()==='replace'`; `writePointers = archiveOnDisk()==='keep-pointers'`; `fastHash = fastHash()`.

- [ ] **Step 2: Component.** Replace the two `<label class="ar-toggle">` checkboxes (`archive-restore-drawer.component.ts:40-42`) with a segmented radio bound to `store.archiveOnDisk` (options: *Keep files only* / *Keep files + pointers* / *Replace with pointers*) plus one `⚡ Fast hash` toggle bound to `store.fastHash`. Remove the "mutually exclusive" `ar-note`. Keep the existing `data-testid` naming convention; add `data-testid="seg-on-disk"` and `data-testid="toggle-fast-hash"`.

- [ ] **Step 3: E2E.** Update `archive.spec.ts` (`:14-19`) to drive the radio instead of asserting the mutual-exclusion behavior; assert the three on-disk options render and `toggle-fast-hash` toggles.

- [ ] **Step 4: Api default.** In `JobRunner.cs` (`:60-61,:125`) rename `noPointers` plumbing to `writePointers` (default `false`) to match Core; keep `removeLocal` implying pointers (Core enforces it, host just passes flags).

- [ ] **Step 5: Build + test**

Run: `cd src/Arius.Web && npm run build && npx playwright test e2e/specs/archive.spec.ts` and `dotnet build src/Arius.Api`.
Expected: build + e2e pass.

- [ ] **Step 6: Commit**

```bash
git add src/Arius.Web/src src/Arius.Web/e2e/specs/archive.spec.ts src/Arius.Api/Jobs/JobRunner.cs
git commit -m "feat(web,api): archive on-disk radio + fast-hash toggle; align pointer default"
```

---

### Task 13: Docs + Synology benchmark

**Files:**
- Create: `docs/decisions/adr-0021-opt-in-change-detection-hashcache.md`
- Create: `docs/design/core/shared/hashcache.md`
- Modify: `docs/design/core/features/archive-command.md`, `docs/design/core/shared/encryption.md`, `docs/glossary.md`, `AGENTS.md`, `README.md`

- [ ] **Step 1: ADR-0021** (use `docs/decisions/adr-template.md`). Record: opt-in heuristic trade; the two per-path failure modes; the disposable-local-cache + *not-concurrently-multi-platform* invariant; the **platform signal mapping** (POSIX `st_ctim`/`st_ino`/`st_dev` ↔ Windows `ChangeTime`/128-bit `FileId`/`VolumeSerialNumber`, incl. `SetFileTime` can't set `ChangeTime`); network-FS → floor; cold = full-hash; sparse-fp coverage limit + the file-type/audit seam.

- [ ] **Step 2: `docs/design/core/shared/hashcache.md`** — follow the design-doc shape used by `chunk-index.md`/`filetree.md` (Purpose / How it works / Key invariants / Why this shape / Open seams). Link from `docs/design/README.md` and `docs/glossary.md` (new terms: **hashcache**, **sparse fingerprint**, **fast-hash**).

- [ ] **Step 3: Update `archive-command.md`** Stage 2 to describe the `--fast-hash` ladder + cache population; add a sentence to `encryption.md` that the content hash may be served from the hashcache under `--fast-hash`.

- [ ] **Step 4: `AGENTS.md`** — add the invariant under a relevant section: *"An archive is local and never concurrently multi-platform; each machine's hashcache is independent and signals are only compared within the same filesystem. Sequential cross-platform use (archive Windows → restore Linux → archive Windows) is supported."*

- [ ] **Step 5: `README.md`** — one human-facing paragraph: `--fast-hash` for big stable archives, and pointers now opt-in via `--write-pointers`.

- [ ] **Step 6: Benchmark on the Synology image** — using `src/Arius.Benchmarks` (or a one-off harness), measure full-hash vs warm `--fast-hash` on a representative dataset; confirm/tune `B`/`stride`/`Kmax` and that `TryGetChangeSignals` returns POSIX signals (not `null`) on the Synology volume. Record results in the ADR's "Why this shape".

- [ ] **Step 7: Commit**

```bash
git add docs AGENTS.md README.md
git commit -m "docs: ADR-0021 + hashcache design, glossary, AGENTS, README for fast-hash"
```

---

## Self-Review

**Spec coverage** (each spec section → task):
- Opt-in `--fast-hash` → Tasks 6, 7, 9. ✓
- Verdict ladder (miss/fp_algo/size/ctime/fp) → Task 5 (+ Task 7 wiring). ✓
- Sparse fingerprint (size-scaled K, offsets, whole-small-file, combined hash, fp_algo) → Task 2. ✓
- Hashcache SQLite (schema incl. `inode TEXT`, provenance columns) → Task 3. ✓
- Platform signals + network-FS null + Windows mapping → Task 4. ✓
- Cache population on normal runs (inline sampler, zero extra IO) → Tasks 2 (`Sampler`) + 7 (`SparseSamplingStream`). ✓
- Cold = full-hash on miss → falls out of Task 5 (`"cache miss"` → caller full-hashes). ✓
- Logging (per-file + summary) → Task 7. ✓
- `--write-pointers` default + `--remove-local` implication + preserve pointer-only → Task 10; CLI Task 11; hosts Task 12. ✓
- Host impacts (Web pane, Api) → Task 12. ✓
- Invariants / ADR / design / glossary / AGENTS / README / benchmark → Task 13. ✓
- Deferred seams (snapshot seeding, `last_verified` audit, mtime tier) → documented in Task 13's ADR; `last_verified` column exists (Task 3); no code. ✓

**Placeholder scan:** No "TBD"/"add error handling"/"similar to Task N". The native-interop task carries explicit fallbacks rather than placeholders.

**Type consistency:** `IHashCacheService.TryReuse`/`Record` signatures match Task 7's call sites; `FastHashResult.IsHit`/`.Hash`/`.Reason` consistent; `SparseFingerprint.Algo`/`Regions`/`ComputeBySeeking`/`Sampler` consistent across Tasks 2/5/7; `HashCacheEntry` field names match the store SQL and the service. `FileChangeSignals`/`SignalSets` consistent across Tasks 4/5. `WritePointers` replaces `NoPointers` consistently in Tasks 10–12.

**Known risk flagged:** Task 4 native interop (struct layouts, Mono.Posix field names, `statfs` availability) is the one place to verify against the toolchain during implementation; the verdict ladder is correct with `signals == null`, so a partial Task 4 still ships a working sparse-fp floor.
