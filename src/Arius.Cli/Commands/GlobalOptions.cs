using System.CommandLine;

namespace Arius.Cli.Commands;

/// <summary>
/// Shared global options used across all commands.
/// </summary>
internal static class GlobalOptions
{
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
    /// Resolves the repository path from the option value or ARIUS_REPOSITORY env var.
    /// Returns null if not set.
    /// </summary>
    public static string? ResolveRepo(string? optionValue)
        => optionValue ?? Environment.GetEnvironmentVariable("ARIUS_REPOSITORY");

    /// <summary>
    /// Resolves the passphrase from the password file option, ARIUS_PASSWORD env var,
    /// or by prompting interactively.
    /// </summary>
    public static string ResolvePassphrase(string? passwordFile)
    {
        // 1. Environment variable
        var envPassword = Environment.GetEnvironmentVariable("ARIUS_PASSWORD");
        if (!string.IsNullOrEmpty(envPassword))
            return envPassword;

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
