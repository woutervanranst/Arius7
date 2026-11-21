namespace Arius.Api.Features.Downloads;

/// <summary>Download staging folders currently on the server disk and their total size.</summary>
/// <param name="Downloads">Number of staging folders.</param>
/// <param name="Bytes">Total size of the staging folders, in bytes.</param>
public sealed record DownloadUsageDto(int Downloads, long Bytes);
