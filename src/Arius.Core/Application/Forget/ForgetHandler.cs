using Arius.Core.Application.Abstractions;
using Arius.Core.Infrastructure;
using Arius.Core.Models;

namespace Arius.Core.Application.Forget;

// ── Events ────────────────────────────────────────────────────────────────────

public enum ForgetDecision { Keep, Remove }

public sealed record ForgetEvent(
    string         SnapshotId,
    DateTimeOffset SnapshotTime,
    ForgetDecision Decision,
    string         Reason);

// ── Retention policy ──────────────────────────────────────────────────────────

public sealed record RetentionPolicy(
    int?   KeepLast    = null,
    int?   KeepHourly  = null,
    int?   KeepDaily   = null,
    int?   KeepWeekly  = null,
    int?   KeepMonthly = null,
    int?   KeepYearly  = null,
    string? KeepWithin = null,
    IReadOnlyList<string>? KeepTags = null);

// ── Request ───────────────────────────────────────────────────────────────────

public sealed record ForgetRequest(
    string          ConnectionString,
    string          ContainerName,
    string          Passphrase,
    RetentionPolicy Policy,
    bool            DryRun = false) : IStreamRequest<ForgetEvent>;

// ── Handler ───────────────────────────────────────────────────────────────────

public sealed class ForgetHandler : IStreamRequestHandler<ForgetRequest, ForgetEvent>
{
    private readonly Func<string, string, AzureRepository> _repoFactory;

    public ForgetHandler(Func<string, string, AzureRepository> repoFactory)
        => _repoFactory = repoFactory;

    public async IAsyncEnumerable<ForgetEvent> Handle(
        ForgetRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var repo = _repoFactory(request.ConnectionString, request.ContainerName);
        _ = await repo.UnlockAsync(request.Passphrase, cancellationToken);

        // Load all snapshots (newest first)
        var snapshots = new List<Snapshot>();
        await foreach (var doc in repo.ListSnapshotDocumentsAsync(cancellationToken))
            snapshots.Add(doc.Snapshot);

        snapshots.Sort((a, b) => b.Time.CompareTo(a.Time));

        var keep = DetermineKeep(snapshots, request.Policy);

        foreach (var snapshot in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var shouldKeep  = keep.Contains(snapshot.Id.Value);
            var decision    = shouldKeep ? ForgetDecision.Keep : ForgetDecision.Remove;
            var reason      = shouldKeep ? "matches retention policy" : "not covered by retention policy";

            yield return new ForgetEvent(snapshot.Id.Value, snapshot.Time, decision, reason);

            if (!shouldKeep && !request.DryRun)
                await repo.DeleteSnapshotAsync(snapshot.Id, cancellationToken);
        }
    }

    // ── Retention logic ───────────────────────────────────────────────────────

    private static HashSet<string> DetermineKeep(
        IReadOnlyList<Snapshot> snapshotsByNewest,
        RetentionPolicy policy)
    {
        var keep = new HashSet<string>(StringComparer.Ordinal);

        // --keep-last N
        if (policy.KeepLast is > 0)
        {
            foreach (var s in snapshotsByNewest.Take(policy.KeepLast.Value))
                keep.Add(s.Id.Value);
        }

        // --keep-hourly N — keep the newest snapshot per hour
        if (policy.KeepHourly is > 0)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var count = 0;
            foreach (var s in snapshotsByNewest)
            {
                var bucket = s.Time.ToString("yyyy-MM-dd HH");
                if (seen.Add(bucket))
                {
                    keep.Add(s.Id.Value);
                    if (++count >= policy.KeepHourly.Value) break;
                }
            }
        }

        // --keep-daily N
        if (policy.KeepDaily is > 0)
        {
            var seen  = new HashSet<string>(StringComparer.Ordinal);
            var count = 0;
            foreach (var s in snapshotsByNewest)
            {
                var bucket = s.Time.ToString("yyyy-MM-dd");
                if (seen.Add(bucket))
                {
                    keep.Add(s.Id.Value);
                    if (++count >= policy.KeepDaily.Value) break;
                }
            }
        }

        // --keep-weekly N
        if (policy.KeepWeekly is > 0)
        {
            var seen  = new HashSet<string>(StringComparer.Ordinal);
            var count = 0;
            foreach (var s in snapshotsByNewest)
            {
                var d      = s.Time.UtcDateTime;
                var bucket = $"{d.Year}-W{System.Globalization.ISOWeek.GetWeekOfYear(d):D2}";
                if (seen.Add(bucket))
                {
                    keep.Add(s.Id.Value);
                    if (++count >= policy.KeepWeekly.Value) break;
                }
            }
        }

        // --keep-monthly N
        if (policy.KeepMonthly is > 0)
        {
            var seen  = new HashSet<string>(StringComparer.Ordinal);
            var count = 0;
            foreach (var s in snapshotsByNewest)
            {
                var bucket = s.Time.ToString("yyyy-MM");
                if (seen.Add(bucket))
                {
                    keep.Add(s.Id.Value);
                    if (++count >= policy.KeepMonthly.Value) break;
                }
            }
        }

        // --keep-yearly N
        if (policy.KeepYearly is > 0)
        {
            var seen  = new HashSet<string>(StringComparer.Ordinal);
            var count = 0;
            foreach (var s in snapshotsByNewest)
            {
                var bucket = s.Time.ToString("yyyy");
                if (seen.Add(bucket))
                {
                    keep.Add(s.Id.Value);
                    if (++count >= policy.KeepYearly.Value) break;
                }
            }
        }

        // --keep-within DURATION (simple: parse "30d", "1w", "12h")
        if (!string.IsNullOrEmpty(policy.KeepWithin))
        {
            var cutoff = ParseDuration(policy.KeepWithin);
            if (cutoff.HasValue)
            {
                var threshold = DateTimeOffset.UtcNow - cutoff.Value;
                foreach (var s in snapshotsByNewest.Where(s => s.Time >= threshold))
                    keep.Add(s.Id.Value);
            }
        }

        // --keep-tag TAG
        if (policy.KeepTags is { Count: > 0 })
        {
            foreach (var s in snapshotsByNewest)
            {
                if (s.Tags.Any(t => policy.KeepTags.Contains(t, StringComparer.OrdinalIgnoreCase)))
                    keep.Add(s.Id.Value);
            }
        }

        return keep;
    }

    private static TimeSpan? ParseDuration(string duration)
    {
        if (string.IsNullOrEmpty(duration)) return null;
        var num  = duration[..^1];
        var unit = duration[^1];
        if (!int.TryParse(num, out var n)) return null;
        return unit switch
        {
            'h' => TimeSpan.FromHours(n),
            'd' => TimeSpan.FromDays(n),
            'w' => TimeSpan.FromDays(n * 7),
            'm' => TimeSpan.FromDays(n * 30),
            'y' => TimeSpan.FromDays(n * 365),
            _   => null
        };
    }
}
