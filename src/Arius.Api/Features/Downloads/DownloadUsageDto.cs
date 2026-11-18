namespace Arius.Api.Features.Downloads;

/// <summary>Download staging folders currently on the server disk and their total size.</summary>
public sealed record DownloadUsageDto(int Downloads, long Bytes);
