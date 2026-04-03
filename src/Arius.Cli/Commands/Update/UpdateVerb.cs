using System.Diagnostics;
using Spectre.Console;
using System.CommandLine;
using System.Text.Json;

namespace Arius.Cli.Commands.Update;

internal static class UpdateVerb
{

    /// <summary>
    /// Builds the "update" command that checks GitHub for a newer release and, if available,
    /// downloads the appropriate platform asset and replaces the running executable.
    /// </summary>
    /// <returns>Process exit code: <c>0</c> on success, <c>1</c> on failure.</returns>
    internal static Command Build()
    {
        var cmd = new Command("update", "Check for updates and apply them");

        cmd.SetAction(async (parseResult, ct) =>
        {
            const string repoOwner = "woutervanranst";
            const string repoName  = "Arius7";

            try
            {
                var currentVersion = typeof(AssemblyMarker).Assembly
                    .GetName().Version ?? new Version(0, 0, 0);

                AnsiConsole.MarkupLine($"[dim]Current version: {currentVersion.Major}.{currentVersion.Minor}.{currentVersion.Build}[/]");
                AnsiConsole.MarkupLine("[dim]Checking for updates...[/]");

                using var http = new HttpClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Arius-CLI");

                var json = await http.GetStringAsync(
                    $"https://api.github.com/repos/{repoOwner}/{repoName}/releases/latest", ct);

                using var doc  = JsonDocument.Parse(json);
                var root       = doc.RootElement;

                var tag = root.TryGetProperty("tag_name", out var tagProp)
                    ? tagProp.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(tag))
                {
                    AnsiConsole.MarkupLine("[red]Could not determine latest version.[/]");
                    return 1;
                }

                var versionStr = tag.TrimStart('v');

                if (!Version.TryParse(versionStr, out var latestVersion))
                {
                    AnsiConsole.MarkupLine($"[red]Could not parse version from tag '{tag}'.[/]");
                    return 1;
                }

                if (latestVersion <= currentVersion)
                {
                    AnsiConsole.MarkupLine("[green]You are running the latest version.[/]");
                    return 0;
                }

                AnsiConsole.MarkupLine($"[blue]New version available: {versionStr}[/]");

                var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
                var ridToAsset = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["win-x64"]   = "arius-win-x64.exe",
                    ["osx-x64"]   = "arius-osx-x64",
                    ["osx-arm64"] = "arius-osx-arm64",
                    ["linux-x64"] = "arius-linux-x64",
                };

                if (!ridToAsset.TryGetValue(rid, out var assetName))
                {
                    AnsiConsole.MarkupLine($"[red]Unsupported platform '{rid}'. Supported: {string.Join(", ", ridToAsset.Keys)}[/]");
                    return 1;
                }

                if (!root.TryGetProperty("assets", out var assetsProp) ||
                    assetsProp.ValueKind != JsonValueKind.Array)
                {
                    AnsiConsole.MarkupLine("[red]Could not read assets from release.[/]");
                    return 1;
                }

                string? downloadUrl = null;
                foreach (var asset in assetsProp.EnumerateArray())
                {
                    if (asset.TryGetProperty("name", out var nameProp) &&
                        nameProp.GetString() == assetName &&
                        asset.TryGetProperty("browser_download_url", out var urlProp))
                    {
                        downloadUrl = urlProp.GetString();
                        break;
                    }
                }

                if (string.IsNullOrEmpty(downloadUrl))
                {
                    AnsiConsole.MarkupLine($"[red]Asset '{assetName}' not found in release.[/]");
                    return 1;
                }

                var tempDir  = Path.Combine(Path.GetTempPath(), $"arius-update-{versionStr}");
                var tempFile = Path.Combine(tempDir, assetName);
                Directory.CreateDirectory(tempDir);

                await AnsiConsole.Progress()
                    .AutoClear(false)
                    .Columns(
                        new TaskDescriptionColumn(),
                        new ProgressBarColumn(),
                        new PercentageColumn(),
                        new SpinnerColumn())
                    .StartAsync(async ctx =>
                    {
                        var task = ctx.AddTask("[green]Downloading update[/]");
                        using var response = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                        response.EnsureSuccessStatusCode();
                        var totalBytes = response.Content.Headers.ContentLength ?? -1;
                        if (totalBytes > 0) task.MaxValue = totalBytes;

                        await using var stream = await response.Content.ReadAsStreamAsync(ct);
                        await using var file   = File.Create(tempFile);
                        var buffer     = new byte[81920];
                        long downloaded = 0;
                        int  bytesRead;
                        while ((bytesRead = await stream.ReadAsync(buffer, ct)) > 0)
                        {
                            await file.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                            downloaded += bytesRead;
                            if (totalBytes > 0) task.Value = downloaded;
                        }
                        task.Value = task.MaxValue;
                    });

                var currentExe = Environment.ProcessPath!;

                if (OperatingSystem.IsWindows())
                {
                    var helperScriptPath = Path.Combine(tempDir, "windows-update-after-exit.ps1");
                    File.WriteAllText(helperScriptPath, LoadEmbeddedWindowsUpdateScript());

                    if (Process.Start(new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{helperScriptPath}\" -PidToWait {Environment.ProcessId} -SourcePath \"{tempFile}\" -DestinationPath \"{currentExe}\" -TempDir \"{tempDir}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    }) is null)
                    {
                        throw new InvalidOperationException("Could not launch the Windows update helper.");
                    }

                    AnsiConsole.MarkupLine($"[green]Downloaded {versionStr}. Restart arius in a moment.[/]");
                    return 0;
                }

                File.Move(tempFile, currentExe, true);
                File.SetUnixFileMode(currentExe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch
                {
                }

                AnsiConsole.MarkupLine($"[green]Updated to {versionStr}. Please restart arius.[/]");
                return 0;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Update failed:[/] {ex.Message}");
                return 1;
            }
        });

        return cmd;
    }

    private static string LoadEmbeddedWindowsUpdateScript()
    {
        var assembly = typeof(UpdateVerb).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("WindowsUpdateAfterExit.ps1", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Embedded Windows update script not found.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Could not open embedded Windows update script.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
