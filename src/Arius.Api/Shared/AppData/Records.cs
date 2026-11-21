using System.Text.Json.Serialization;
using Arius.Api.Shared.Jobs;

namespace Arius.Api.Shared.AppData;

/// <summary>A configured Azure Storage account. <see cref="EncryptedAccountKey"/> is Data Protection ciphertext.</summary>
public sealed record AccountRecord(long Id, string Name, string? EncryptedAccountKey, DateTimeOffset CreatedAt);

/// <summary>A managed repository (one blob container). <see cref="EncryptedPassphrase"/> is Data Protection ciphertext.</summary>
public sealed record RepositoryRecord(
    long            Id,
    string          Alias,
    string          Container,
    long            AccountId,
    string?         LocalPath,
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
    string? LocalPath);

/// <summary>A one-off or scheduled archive, restore or download run.</summary>
public sealed record JobRecord(
    string          Id,
    long            RepositoryId,
    string          Kind,        // archive | restore | download
    string          Trigger,     // one-off | schedule
    string          Status,      // see JobStatuses
    double          Pct,
    string?         Detail,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string?         StateJson = null,   // serialized live JobSnapshot, refreshed while the job runs
    string?         Outcome = null);    // serialized terminal JobOutcome, written on completion

/// <summary>A file collected by a download job. <see cref="Ready"/>: staged on the server; <see cref="Downloaded"/>: fetched by the browser.</summary>
public sealed record DownloadFile(string Path, long Size, bool Ready = false, bool Downloaded = false);

/// <summary>Server-wide clean-up of downloaded files: whether it runs and how many days after a download job finished.</summary>
/// <param name="CleanupEnabled">Whether staged files are deleted automatically.</param>
/// <param name="CleanupDays">Days after a download job finishes before its staged files are deleted, from 1 to 3650.</param>
public sealed record DownloadSettings(bool CleanupEnabled, int CleanupDays)
{
    public static readonly DownloadSettings Default = new(true, 7);
}

/// <summary>Server-wide cost confirmation: a restore or download with every chunk in an online tier and an
/// estimated cost (EUR) below <c>autoAcceptBelow</c> starts without asking; 0 always asks.</summary>
/// <param name="AutoAcceptBelow">Threshold in EUR, from 0 to 1000 with at most two decimal places.</param>
public sealed record CostSettings(decimal AutoAcceptBelow)
{
    public static readonly CostSettings Default = new(0.01m);
}

/// <summary>A cron schedule that fires archive jobs for a repository.</summary>
public sealed record ScheduleRecord(
    long            Id,
    long            RepositoryId,
    string          Cron,
    string          Kind,
    bool            Enabled,
    DateTimeOffset? NextRun);

/// <summary>
/// The five archive/restore options as set at one level of the defaults hierarchy (global settings or one
/// repository's properties). <c>null</c> means "not set at this level" and falls through to the next level.
/// </summary>
/// <param name="Tier">Archive: storage tier for uploaded chunks, <c>hot</c>, <c>cool</c>, <c>cold</c> or <c>archive</c>.</param>
/// <param name="OnDisk">Archive: what happens to local files. <c>keep</c> leaves them as they are, <c>keep-pointers</c> also
/// writes a pointer file next to each file, and <c>replace</c> writes pointer files and deletes the binary files.</param>
/// <param name="FastHash">Archive: reuse cached hashes of unchanged files instead of reading every file again.</param>
/// <param name="Overwrite">Restore: overwrite existing local files.</param>
/// <param name="NoPointers">Restore: do not write pointer files next to restored files.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DefaultValues(string? Tier, string? OnDisk, bool? FastHash, bool? Overwrite, bool? NoPointers)
{
    /// <summary>Nothing set at this level.</summary>
    public static readonly DefaultValues None = new(null, null, null, null, null);
}

/// <summary>An effective option value and the level it came from (<c>factory</c> | <c>settings</c> | <c>repository</c>).</summary>
public sealed record ResolvedValue<T>(T Value, string Source);

/// <summary>The effective value of each option after resolving repository, then global settings, then factory.</summary>
public sealed record ResolvedDefaults(
    ResolvedValue<string> Tier,
    ResolvedValue<string> OnDisk,
    ResolvedValue<bool>   FastHash,
    ResolvedValue<bool>   Overwrite,
    ResolvedValue<bool>   NoPointers);
