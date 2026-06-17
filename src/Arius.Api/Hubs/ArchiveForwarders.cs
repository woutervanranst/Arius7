using Arius.Api.Jobs;
using Arius.Core.Features.ArchiveCommand;
using Mediator;

namespace Arius.Api.Hubs;

// Each forwarder pushes one Arius.Core archive event to the running job's SignalR group as a console
// log line + a progress update. Resolved from the per-job provider (so they target that job's sink).
// The provider source generator auto-registers these INotificationHandler<T> implementations.

public sealed class ScanCompleteForwarder(JobSink sink) : INotificationHandler<ScanCompleteEvent>
{
    public ValueTask Handle(ScanCompleteEvent n, CancellationToken ct)
    {
        sink.SetTotalFiles(n.TotalFiles);
        sink.Log($"Indexed {n.TotalFiles} entries · {JobFormat.Bytes(n.TotalBytes)}", "info");
        sink.ReportArchive(5);
        return ValueTask.CompletedTask;
    }
}

public sealed class FileHashedForwarder(JobSink sink) : INotificationHandler<FileHashedEvent>
{
    public ValueTask Handle(FileHashedEvent n, CancellationToken ct)
    {
        sink.IncHashed();
        sink.ReportArchive();
        return ValueTask.CompletedTask;
    }
}

public sealed class TarBundleSealingForwarder(JobSink sink) : INotificationHandler<TarBundleSealingEvent>
{
    public ValueTask Handle(TarBundleSealingEvent n, CancellationToken ct)
    {
        sink.Log($"  sealing tar bundle · {n.EntryCount} files · {JobFormat.Bytes(n.TarByteSize)}", "meta");
        return ValueTask.CompletedTask;
    }
}

public sealed class TarBundleUploadedForwarder(JobSink sink) : INotificationHandler<TarBundleUploadedEvent>
{
    public ValueTask Handle(TarBundleUploadedEvent n, CancellationToken ct)
    {
        sink.IncUploaded(n.StoredSize);
        sink.Log($"  ✓ tar bundle uploaded · {n.EntryCount} files → {JobFormat.Bytes(n.StoredSize)}", "ok");
        sink.ReportArchive();
        return ValueTask.CompletedTask;
    }
}

public sealed class ChunkUploadedForwarder(JobSink sink) : INotificationHandler<ChunkUploadedEvent>
{
    public ValueTask Handle(ChunkUploadedEvent n, CancellationToken ct)
    {
        sink.IncUploaded(n.StoredSize);
        sink.Log($"  ✓ {n.ChunkHash.Short8} → {JobFormat.Bytes(n.StoredSize)}", "ok");
        sink.ReportArchive();
        return ValueTask.CompletedTask;
    }
}

public sealed class SnapshotCreatedForwarder(JobSink sink) : INotificationHandler<SnapshotCreatedEvent>
{
    public ValueTask Handle(SnapshotCreatedEvent n, CancellationToken ct)
    {
        sink.Log($"Writing manifest · snapshot {n.FileCount} files", "info");
        sink.ReportArchive(100);
        return ValueTask.CompletedTask;
    }
}
