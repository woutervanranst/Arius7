namespace Arius.Api.Shared.Jobs;

[SharedWithinAssembly]
internal static class JobFormat
{
    // Matches Arius.Web's formatBytes, so a figure reads the same in the job log and in the UI.
    public static string Bytes(long b)
        => b >= 1_000_000_000_000 ? $"{b / 1e12:0.00} TB"
         : b >= 1_000_000_000 ? $"{b / 1e9:0.00} GB"
         : b >= 1_000_000 ? $"{b / 1e6:0.0} MB"
         : b >= 1000 ? $"{b / 1e3:0} KB"
         : $"{b} B";
}
