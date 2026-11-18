namespace Arius.Api.Features.Downloads;

/// <summary>A finished download that still has staged files the browser has not fetched.</summary>
public sealed record PendingDownloadDto(string JobId, int ReadyFiles, int TotalFiles, int RemainingFiles, long RemainingBytes);
