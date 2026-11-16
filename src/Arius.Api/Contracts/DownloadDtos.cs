namespace Arius.Api.Contracts;

/// <summary>One collected file in a start-download request: repository-relative path and its size.</summary>
public sealed record DownloadFileRequest(string Path, long Size);

/// <summary>One collected file of a download job: whether it is staged on the server and whether the browser has fetched it.</summary>
public sealed record DownloadFileDto(string Path, long Size, bool Ready, bool Downloaded);

/// <summary>A download job's files plus whether its staging folder is gone and when it will be cleaned up (null when never).</summary>
public sealed record DownloadStateDto(IReadOnlyList<DownloadFileDto> Files, bool CleanedUp, DateTimeOffset? CleanupAt);

/// <summary>A finished download that still has staged files the browser has not fetched.</summary>
public sealed record PendingDownloadDto(string JobId, int ReadyFiles, int TotalFiles, int RemainingFiles, long RemainingBytes);

/// <summary>Download staging folders currently on the server disk and their total size.</summary>
public sealed record DownloadUsageDto(int Downloads, long Bytes);
