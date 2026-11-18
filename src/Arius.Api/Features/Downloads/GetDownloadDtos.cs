namespace Arius.Api.Features.Downloads;

/// <summary>One collected file of a download job: whether it is staged on the server and whether the browser has fetched it.</summary>
public sealed record DownloadFileDto(string Path, long Size, bool Ready, bool Downloaded);

/// <summary>A download job's files plus whether its staging folder is gone and when it will be cleaned up (null when never).</summary>
public sealed record DownloadStateDto(IReadOnlyList<DownloadFileDto> Files, bool CleanedUp, DateTimeOffset? CleanupAt);
