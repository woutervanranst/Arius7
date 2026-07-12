using Arius.Api.Jobs;
using Arius.Core.Features.ArchiveCommand;
using Mediator;

namespace Arius.Api.Hubs;

// Each forwarder folds one Arius.Core archive event into the job's JobSink byte aggregate.

public sealed class ScanCompleteForwarder(JobSink sink) : INotificationHandler<ScanCompleteEvent>
{
    public ValueTask Handle(ScanCompleteEvent n, CancellationToken ct)
    {
        sink.SetTotals(n.TotalFiles, n.TotalBytes);
        return ValueTask.CompletedTask;
    }
}

public sealed class FileScannedForwarder(JobSink sink) : INotificationHandler<FileScannedEvent>
{
    public ValueTask Handle(FileScannedEvent n, CancellationToken ct) { sink.AddScanned(n.FileSize); return ValueTask.CompletedTask; }
}

public sealed class FileHashingForwarder(JobSink sink) : INotificationHandler<FileHashingEvent>
{
    public ValueTask Handle(FileHashingEvent n, CancellationToken ct) { sink.SetPhase("hash-route"); sink.AddHashed(n.FileSize); return ValueTask.CompletedTask; }
}

public sealed class FileDedupedForwarder(JobSink sink) : INotificationHandler<FileDedupedEvent>
{
    public ValueTask Handle(FileDedupedEvent n, CancellationToken ct) { sink.AddDeduped(n.OriginalSize); return ValueTask.CompletedTask; }
}

public sealed class TarBundleSealingForwarder(JobSink sink) : INotificationHandler<TarBundleSealingEvent>
{
    public ValueTask Handle(TarBundleSealingEvent n, CancellationToken ct)
    {
        sink.RememberTar(n.TarHash, n.UncompressedSize);
        return ValueTask.CompletedTask;
    }
}

public sealed class TarBundleUploadedForwarder(JobSink sink) : INotificationHandler<TarBundleUploadedEvent>
{
    public ValueTask Handle(TarBundleUploadedEvent n, CancellationToken ct)
    {
        sink.AddUploadedTar(n.TarHash);
        return ValueTask.CompletedTask;
    }
}

public sealed class ChunkUploadedForwarder(JobSink sink) : INotificationHandler<ChunkUploadedEvent>
{
    public ValueTask Handle(ChunkUploadedEvent n, CancellationToken ct)
    {
        sink.AddUploaded(n.ChunkHash, n.StoredSize, n.OriginalSize);
        return ValueTask.CompletedTask;
    }
}

public sealed class ChunkUploadingForwarder(JobSink sink) : INotificationHandler<ChunkUploadingEvent>
{
    public ValueTask Handle(ChunkUploadingEvent n, CancellationToken ct)
    {
        sink.SetPhase("upload");
        sink.AddQueuedNew(n.Size);   // additive "new bytes to upload" total (upload-progress denominator)
        return ValueTask.CompletedTask;
    }
}

public sealed class SnapshotCreatedForwarder(JobSink sink) : INotificationHandler<SnapshotCreatedEvent>
{
    public ValueTask Handle(SnapshotCreatedEvent n, CancellationToken ct) => ValueTask.CompletedTask;
}

public sealed class FinalizingSnapshotForwarder(JobSink sink) : INotificationHandler<FinalizingSnapshotEvent>
{
    public ValueTask Handle(FinalizingSnapshotEvent n, CancellationToken ct) { sink.SetPhase("snapshot"); return ValueTask.CompletedTask; }
}
