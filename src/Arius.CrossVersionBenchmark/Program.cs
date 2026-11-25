using System.Globalization;
using Arius.CrossVersionBenchmark;
using Azure.Storage;
using Azure.Storage.Blobs;
using BenchmarkDotNet.Running;

// ─────────────────────────────────────────────────────────────────────────────
// Arius cross-version archive benchmark (BenchmarkDotNet)
//
// Wall-clock comparison of `archive` between two builds with incompatible on-blob
// formats: the original v5 (github.com/woutervanranst/Arius) and the v7 rewrite
// (this repo). BenchmarkDotNet drives the real CLIs via the out-of-process toolchain
// using RunStrategy.ColdStart — one fresh-from-scratch archive into a brand-new
// container per iteration. Peak memory (/usr/bin/time -l), resulting blob size and
// source size are added as custom columns fed by per-version CSV files.
//
// Usage: see README.md. Special mode:
//   --delete-prefix <p>   Delete every container whose name starts with <p>, then exit
//                         (uses ARIUS_ACCOUNT / ARIUS_KEY). For cleaning up after a crash.
// ─────────────────────────────────────────────────────────────────────────────

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

// ── Cleanup mode ─────────────────────────────────────────────────────────────
var deleteIdx = Array.IndexOf(args, "--delete-prefix");
if (deleteIdx >= 0)
{
    var prefix  = args[deleteIdx + 1];
    var account = Environment.GetEnvironmentVariable("ARIUS_ACCOUNT") ?? Environment.GetEnvironmentVariable("ARIUS_ACCOUNT_NAME")!;
    var key     = Environment.GetEnvironmentVariable("ARIUS_KEY") ?? Environment.GetEnvironmentVariable("ARIUS_ACCOUNT_KEY")!;
    var service = new BlobServiceClient(new Uri($"https://{account}.blob.core.windows.net"), new StorageSharedKeyCredential(account, key));

    Console.WriteLine($"Deleting containers with prefix '{prefix}' on {account}...");
    var deleted = 0;
    foreach (var item in service.GetBlobContainers(prefix: prefix))
    {
        try { service.DeleteBlobContainer(item.Name); Console.WriteLine($"  deleted {item.Name}"); deleted++; }
        catch (Exception ex) { Console.WriteLine($"  WARN {item.Name}: {ex.Message}"); }
    }
    Console.WriteLine($"Done ({deleted} deleted).");
    return 0;
}

// ── Benchmark mode ───────────────────────────────────────────────────────────
BenchmarkSettings.Parse(args);

Console.WriteLine("Arius cross-version archive benchmark (BenchmarkDotNet, out-of-process)");
Console.WriteLine($"  v5 CLI    : {BenchmarkSettings.V5CliDll}");
Console.WriteLine($"  v7 CLI    : {BenchmarkSettings.V7CliDll}");
Console.WriteLine($"  source    : {BenchmarkSettings.SourceDirectory}");
Console.WriteLine($"  account   : {BenchmarkSettings.Account}  tier {BenchmarkSettings.Tier}");
Console.WriteLine($"  run id    : {BenchmarkSettings.RunId}  ({BenchmarkSettings.IterationCount} runs/version)");
Console.WriteLine($"  output    : {BenchmarkSettings.OutputDirectory}");
Console.WriteLine($"  containers: {(BenchmarkSettings.KeepContainers ? "KEPT after run" : "deleted after run")}");
Console.WriteLine();

// Overwrite this process's environment with the resolved (absolutized) values so the
// BenchmarkDotNet child inherits correct paths even if the caller passed relative ones.
foreach (var (key, value) in BenchmarkSettings.ToEnvironment())
    Environment.SetEnvironmentVariable(key, value);

BenchmarkRunner.Run<CrossVersionArchiveBenchmark>(BenchmarkConfig.Create());
return 0;
