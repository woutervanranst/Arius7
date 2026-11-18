namespace Arius.Api.Contracts;

/// <summary>One collected file in a start-download request: repository-relative path and its size.</summary>
public sealed record DownloadFileRequest(string Path, long Size);
