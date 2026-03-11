using System.CommandLine;
using Microsoft.Extensions.Configuration;

namespace Arius.Cli.Commands;

/// <summary>
/// Shared global options used across all commands.
/// </summary>
internal static class GlobalOptions
{
    /// <summary>
    /// Set once during startup. Provides access to user secrets and environment variables
    /// as a unified configuration source.
    /// </summary>
    public static IConfiguration? Configuration { get; set; }

    public static readonly Option<string?> Repo = new("--repo", "-r")
    {
        Description = "Repository path (or set ARIUS_REPOSITORY environment variable)"
    };

    public static readonly Option<string?> PasswordFile = new("--password-file")
    {
        Description = "Path to file containing repository passphrase"
    };

    public static readonly Option<bool> Json = new("--json")
    {
        Description = "Output results as JSON"
    };

    public static readonly Option<bool> Yes = new("--yes", "-y")
    {
        Description = "Skip confirmation prompts"
    };

    public static readonly Option<bool> Verbose = new("--verbose", "-v")
    {
        Description = "Enable verbose output"
    };

    /// <summary>
    /// Resolves the repository connection string.
    /// Priority: --repo option → ARIUS_REPOSITORY env var → user secrets.
    /// Returns null if not set anywhere.
    /// </summary>
    public static string? ResolveRepo(string? optionValue)
        => optionValue
        ?? Configuration?["ARIUS_REPOSITORY"]
        ?? Environment.GetEnvironmentVariable("ARIUS_REPOSITORY");

    /// <summary>
    /// Resolves the passphrase.
    /// Priority: ARIUS_PASSWORD env var / user secret → --password-file → interactive prompt.
    /// </summary>
    public static string ResolvePassphrase(string? passwordFile)
    {
        // 1. Configuration (covers env var AND user secrets in one call)
        var configured = Configuration?["ARIUS_PASSWORD"]
                      ?? Environment.GetEnvironmentVariable("ARIUS_PASSWORD");
        if (!string.IsNullOrEmpty(configured))
            return configured;

        // 2. Password file
        if (!string.IsNullOrEmpty(passwordFile) && File.Exists(passwordFile))
            return File.ReadAllText(passwordFile).Trim();

        // 3. Interactive prompt (hidden input)
        Console.Write("Enter repository passphrase: ");
        var passphrase = ReadHiddenInput();
        Console.WriteLine();
        return passphrase;
    }

    private static string ReadHiddenInput()
    {
        var chars = new System.Text.StringBuilder();
        ConsoleKeyInfo key;
        do
        {
            key = Console.ReadKey(intercept: true);
            if (key.Key != ConsoleKey.Enter && key.Key != ConsoleKey.Backspace)
            {
                chars.Append(key.KeyChar);
            }
            else if (key.Key == ConsoleKey.Backspace && chars.Length > 0)
            {
                chars.Remove(chars.Length - 1, 1);
            }
        } while (key.Key != ConsoleKey.Enter);

        return chars.ToString();
    }
}
