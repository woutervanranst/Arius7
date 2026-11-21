namespace Arius.Api.Features.Downloads;

/// <summary>A finished download that still has staged files the browser has not fetched.</summary>
/// <param name="JobId">Download job ID.</param>
/// <param name="ReadyFiles">Number of staged files.</param>
/// <param name="TotalFiles">Number of files the job collected.</param>
/// <param name="RemainingFiles">Number of staged files not fetched yet.</param>
/// <param name="RemainingBytes">Total size of the staged files not fetched yet, in bytes.</param>
public sealed record PendingDownloadDto(string JobId, int ReadyFiles, int TotalFiles, int RemainingFiles, long RemainingBytes);
