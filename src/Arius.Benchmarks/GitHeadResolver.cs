using System.Diagnostics;

namespace Arius.Benchmarks;

internal static class GitHeadResolver
{
    public static string Resolve(string repositoryRoot)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "git",
            ArgumentList = { "-C", repositoryRoot, "rev-parse", "HEAD" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        if (process is null)
            return "unknown";

        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();

        return process.ExitCode == 0 && output.Length > 0
            ? output
            : "unknown";
    }
}
