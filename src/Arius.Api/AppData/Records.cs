namespace Arius.Api.AppData;

/// <summary>A configured Azure Storage account. <see cref="EncryptedAccountKey"/> is Data-Protection ciphertext.</summary>
public sealed record AccountRecord(long Id, string Name, string? EncryptedAccountKey, DateTimeOffset CreatedAt);

/// <summary>A managed repository (one blob container). <see cref="EncryptedPassphrase"/> is Data-Protection ciphertext.</summary>
public sealed record RepositoryRecord(
    long            Id,
    string          Alias,
    string          Container,
    long            AccountId,
    string?         LocalPath,
    string          DefaultTier,
    string?         RegionHint,
    string?         EncryptedPassphrase,
    DateTimeOffset  CreatedAt);

/// <summary>A repository joined with its account's connection material, secrets decrypted. Never serialized to the client.</summary>
public sealed record RepositoryConnection(
    long    RepositoryId,
    string  Alias,
    string  AccountName,
    string? AccountKey,
    string  Container,
    string? Passphrase,
    string? LocalPath,
    string  DefaultTier);

/// <summary>A one-off or scheduled archive/restore run.</summary>
public sealed record JobRecord(
    string          Id,
    long            RepositoryId,
    string          Kind,        // archive | restore
    string          Trigger,     // one-off | schedule
    string          Status,      // queued | running | rehydrating | completed | failed | cancelled
    double          Pct,
    string?         Detail,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

/// <summary>A cron schedule that fires archive jobs for a repository.</summary>
public sealed record ScheduleRecord(
    long            Id,
    long            RepositoryId,
    string          Cron,
    string          Kind,
    bool            Enabled,
    DateTimeOffset? NextRun);
