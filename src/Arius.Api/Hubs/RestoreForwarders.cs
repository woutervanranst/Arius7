using Arius.Api.Jobs;
using Arius.Core.Features.RestoreCommand;
using Mediator;

namespace Arius.Api.Hubs;

// Forwarders for Arius.Core restore events → the running job's SignalR group.

public sealed class SnapshotResolvedForwarder(JobSink sink) : INotificationHandler<SnapshotResolvedEvent>
{
    public ValueTask Handle(SnapshotResolvedEvent n, CancellationToken ct)
    {
        sink.Log($"Resolved snapshot {n.Timestamp:dd MMM yyyy HH:mm}", "meta");
        return ValueTask.CompletedTask;
    }
}

public sealed class TreeTraversalCompleteForwarder(JobSink sink) : INotificationHandler<TreeTraversalCompleteEvent>
{
    public ValueTask Handle(TreeTraversalCompleteEvent n, CancellationToken ct)
    {
        sink.SetRestoreTotals(n.FileCount, n.TotalOriginalSize);
        sink.Log($"✓ {n.FileCount} files · {JobFormat.Bytes(n.TotalOriginalSize)}", "ok");
        return ValueTask.CompletedTask;
    }
}

public sealed class ChunkResolutionCompleteForwarder(JobSink sink) : INotificationHandler<ChunkResolutionCompleteEvent>
{
    public ValueTask Handle(ChunkResolutionCompleteEvent n, CancellationToken ct)
    {
        sink.SetChunkTotals(n.TotalChunks, n.TotalChunkBytes);
        return ValueTask.CompletedTask;
    }
}

public sealed class RehydrationStatusForwarder(JobSink sink) : INotificationHandler<RehydrationStatusEvent>
{
    public ValueTask Handle(RehydrationStatusEvent n, CancellationToken ct)
    {
        sink.SetRehydration(n.Available, n.Rehydrated, n.NeedsRehydration, n.Pending);
        sink.Log($"{n.Available + n.Rehydrated} chunks hydrated · {n.NeedsRehydration} need rehydration", n.NeedsRehydration > 0 ? "warn" : "info");
        return ValueTask.CompletedTask;
    }
}

public sealed class RehydrationStartedForwarder(JobSink sink) : INotificationHandler<RehydrationStartedEvent>
{
    public ValueTask Handle(RehydrationStartedEvent n, CancellationToken ct)
    {
        sink.Log($"Requesting rehydration · {n.ChunkCount} chunks · {JobFormat.Bytes(n.TotalBytes)}", "warn");
        return ValueTask.CompletedTask;
    }
}

public sealed class ChunkDownloadStartedForwarder(JobSink sink) : INotificationHandler<ChunkDownloadStartedEvent>
{
    public ValueTask Handle(ChunkDownloadStartedEvent n, CancellationToken ct)
    {
        sink.Log($"  ↓ {n.ChunkHash.Short8} ({n.Type}) · {JobFormat.Bytes(n.ChunkSize)}", "meta");
        return ValueTask.CompletedTask;
    }
}

public sealed class FileRestoredForwarder(JobSink sink) : INotificationHandler<FileRestoredEvent>
{
    public ValueTask Handle(FileRestoredEvent n, CancellationToken ct)
    {
        sink.AddRestored(n.FileSize);
        sink.Log($"→ {n.RelativePath} ✓", "ok");
        return ValueTask.CompletedTask;
    }
}
