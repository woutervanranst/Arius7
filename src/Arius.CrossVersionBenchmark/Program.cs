using System.Globalization;
using Arius.CrossVersionBenchmark;
using BenchmarkDotNet.Running;
using Humanizer;

// ─────────────────────────────────────────────────────────────────────────────
// Arius cross-version archive benchmark (BenchmarkDotNet)
//
// Wall-clock comparison of `archive` between two builds with incompatible on-blob
// formats: the original v5 (github.com/woutervanranst/Arius) and the v7 rewrite
// (this repo). BenchmarkDotNet drives the real CLIs in-process via ColdStart, one
// fresh-from-scratch archive into a brand-new container per iteration. Peak memory
// (/usr/bin/time -l) and resulting blob size are added as custom columns.
// ─────────────────────────────────────────────────────────────────────────────

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

BenchmarkSettings.Initialize(args);

Console.WriteLine("Arius cross-version archive benchmark (BenchmarkDotNet)");
Console.WriteLine($"  v5 CLI       : {BenchmarkSettings.V5CliDll}");
Console.WriteLine($"  v7 CLI       : {BenchmarkSettings.V7CliDll}");
Console.WriteLine($"  account      : {BenchmarkSettings.Account}  tier {BenchmarkSettings.Tier}");
Console.WriteLine($"  dataset      : {BenchmarkSettings.SourceFileCount:N0} files, {BenchmarkSettings.SourceBytes.Bytes().Humanize("0.##")}");
Console.WriteLine($"  output       : {BenchmarkSettings.OutputDirectory}");
Console.WriteLine();

try
{
    BenchmarkRunner.Run<CrossVersionArchiveBenchmark>(BenchmarkConfig.Create());
}
finally
{
    if (BenchmarkSettings.Cleanup)
    {
        Console.WriteLine();
        Console.WriteLine("Cleaning up benchmark containers...");
        BenchmarkSettings.DeleteCreatedContainers();
    }
}
