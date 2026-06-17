namespace Arius.Api.Contracts;

// ── Accounts ────────────────────────────────────────────────────────────────

/// <summary>A storage account as shown to the client. The account key is never returned.</summary>
public sealed record AccountDto(long Id, string Name, int Repositories, bool HasKey);

public sealed record CreateAccountRequest(string Name, string? AccountKey);

// ── Repositories ──────────────────────────────────────────────────────────────

/// <summary>A repository as shown to the client. Secrets (key, passphrase) are never returned.</summary>
public sealed record RepositoryDto(
    long    Id,
    string  Alias,
    string  Container,
    long    AccountId,
    string  Account,
    string? LocalPath,
    string  DefaultTier);

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

// ── Snapshots / stats (read in Phase 2) ───────────────────────────────────────

public sealed record SnapshotDto(string Version, DateTimeOffset Timestamp, long FileCount);

public sealed record StatsDto(long Files, long OriginalSize, long StoredSize, long UniqueChunks);

// ── Jobs / schedules ──────────────────────────────────────────────────────────

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
    DateTimeOffset? FinishedAt);

public sealed record ScheduleDto(long Id, long RepoId, string Cron, string Kind, bool Enabled, DateTimeOffset? NextRun);

public sealed record CreateScheduleRequest(string Cron, string? Kind);
