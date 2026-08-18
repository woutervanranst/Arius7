namespace Arius.Core.Shared;

/// <summary>
/// The logging conventions the CLI, API, and Explorer share: the <c>ARIUS_LOG_LEVEL</c> contract and the
/// audit-log line format. Serilog-free (the domain library only depends on the MEL abstractions), so the level
/// is exposed as a validated level <b>name</b> and the format as a plain template string, for each host to
/// feed to its own Serilog configuration.
/// </summary>
public static class AriusLogConfig
{
    /// <summary>Environment variable naming the global minimum log level (a Serilog level name).</summary>
    public const string LevelEnvironmentVariable = "ARIUS_LOG_LEVEL";

    /// <summary>The level used when <see cref="LevelEnvironmentVariable"/> is unset or invalid.</summary>
    public const string DefaultLevelName = "Information";

    /// <summary>The audit-log line format (Serilog <c>ExpressionTemplate</c> syntax), shared by the CLI and API so
    /// their logs read identically. <c>[SourceContext]</c> renders as the emitting type's class name (the last
    /// <c>'.'</c>-segment), falling back to <c>Arius</c> for events without a source context.</summary>
    public const string LineTemplate =
        "[{@t:HH:mm:ss.fff}] [{@l:u3}] [T:{ThreadId}] [{Coalesce(Substring(SourceContext, LastIndexOf(SourceContext, '.') + 1), 'Arius')}] {@m}\n{@x}";

    // The Serilog LogEventLevel names, ascending severity.
    private static readonly string[] levelNames = { "Verbose", "Debug", "Information", "Warning", "Error", "Fatal" };

    private static int warned;

    /// <summary>Resolves <see cref="LevelEnvironmentVariable"/> to a canonical Serilog level name. Unset, blank
    /// or unrecognized → <see cref="DefaultLevelName"/>.</summary>
    public static string ResolveLevelName() =>
        ResolveLevelName(Environment.GetEnvironmentVariable(LevelEnvironmentVariable));

    /// <summary>Pure form of <see cref="ResolveLevelName()"/> over an explicit raw value (test seam).</summary>
    internal static string ResolveLevelName(string? rawValue)
    {
        var value = rawValue?.Trim();
        if (string.IsNullOrEmpty(value))
            return DefaultLevelName;

        foreach (var name in levelNames)
            if (string.Equals(name, value, StringComparison.OrdinalIgnoreCase))
                return name;

        // Warn once so a misconfiguration is visible without spamming.
        if (Interlocked.Exchange(ref warned, 1) == 0)
            Console.Error.WriteLine(
                $"[Arius] {LevelEnvironmentVariable}='{rawValue}' is not a valid log level " +
                $"(expected one of {string.Join(", ", levelNames)}); defaulting to {DefaultLevelName}.");

        return DefaultLevelName;
    }
}
