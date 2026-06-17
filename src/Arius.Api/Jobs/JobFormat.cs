namespace Arius.Api.Jobs;

internal static class JobFormat
{
    public static string Bytes(long b)
        => b >= 1_000_000_000 ? $"{b / 1e9:0.00} GB"
         : b >= 1_000_000 ? $"{b / 1e6:0.0} MB"
         : b >= 1000 ? $"{b / 1e3:0} KB"
         : $"{b} B";
}
