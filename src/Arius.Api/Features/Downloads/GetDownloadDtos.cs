namespace Arius.Api.Features.Downloads;

/// <summary>One collected file of a download job: whether it is staged on the server and whether the browser has fetched it.</summary>
/// <param name="Path">Repository-relative path.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="Ready">Whether the file is staged on the server.</param>
/// <param name="Downloaded">Whether the browser has fetched the file.</param>
public sealed record DownloadFileDto(string Path, long Size, bool Ready, bool Downloaded);

/// <summary>A download job's files plus whether its staging folder is gone and when it will be cleaned up (null when never).</summary>
/// <param name="Files">The files the job collected.</param>
/// <param name="CleanedUp">Whether the staging folder has been deleted.</param>
/// <param name="CleanupAt">When automatic clean-up deletes the staging folder, or <c>null</c> if it will not.</param>
public sealed record DownloadStateDto(IReadOnlyList<DownloadFileDto> Files, bool CleanedUp, DateTimeOffset? CleanupAt);
