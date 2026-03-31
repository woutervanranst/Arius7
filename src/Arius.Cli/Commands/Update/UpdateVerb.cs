using Spectre.Console;
using System.CommandLine;

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

                var tagStart = json.IndexOf("\"tag_name\":\"", StringComparison.Ordinal);
                if (tagStart < 0)
                {
                    AnsiConsole.MarkupLine("[red]Could not determine latest version.[/]");
                    return 1;
                }
                tagStart += "\"tag_name\":\"".Length;
                var tagEnd     = json.IndexOf('"', tagStart);
                var tag        = json[tagStart..tagEnd];
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
                string assetName;
                if (rid.Contains("win"))       assetName = "arius-win-x64.exe";
                else if (rid.Contains("osx"))  assetName = "arius-osx-arm64";
                else                           assetName = "arius-linux-x64";

                var assetKey = $"\"name\":\"{assetName}\"";
                var assetIdx = json.IndexOf(assetKey, StringComparison.Ordinal);
                if (assetIdx < 0)
                {
                    AnsiConsole.MarkupLine($"[red]Asset '{assetName}' not found in release.[/]");
                    return 1;
                }

                var urlKey = "\"browser_download_url\":\"";
                var urlIdx = json.IndexOf(urlKey, assetIdx, StringComparison.Ordinal);
                if (urlIdx < 0)
                {
                    AnsiConsole.MarkupLine("[red]Could not find download URL.[/]");
                    return 1;
                }
                urlIdx += urlKey.Length;
                var urlEnd      = json.IndexOf('"', urlIdx);
                var downloadUrl = json[urlIdx..urlEnd];

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
                File.Move(tempFile, currentExe, true);

                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(currentExe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }

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
}
