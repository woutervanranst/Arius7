namespace Arius.Core.Shared;

/// <summary>
/// Cross-host logging conventions shared by the CLI, API, and Explorer: the <c>ARIUS_LOG_LEVEL</c> contract and
/// the audit-log line format. Deliberately free of any Serilog dependency (the domain library uses only the MEL
/// abstractions) — the level is exposed as a validated Serilog level <b>name</b> that each host maps to its own
/// <c>LogEventLevel</c>, and the line format is a plain template string each host feeds to a Serilog
/// <c>ExpressionTemplate</c>. Centralizing it here keeps the three hosts from silently diverging on the env-var
/// name, the default level, or the format.
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

    // The six Serilog LogEventLevel names, ascending severity. Kept as strings (not the Serilog enum) so this
    // shared type stays dependency-free; hosts Enum.Parse the returned name into Serilog.Events.LogEventLevel.
    private static readonly string[] levelNames = { "Verbose", "Debug", "Information", "Warning", "Error", "Fatal" };

    private static int warned;

    /// <summary>Resolves <see cref="LevelEnvironmentVariable"/> to a canonical Serilog level name. Unset or blank
    /// → <see cref="DefaultLevelName"/>. An unrecognized or out-of-range value also falls back to the default
    /// (it never silently disables logging) and warns once to stderr.</summary>
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

        // Fall back rather than throw or feed an undefined enum value downstream (which would filter out every
        // event and silently disable all logging). Warn once so a misconfiguration is visible without spamming.
        if (Interlocked.Exchange(ref warned, 1) == 0)
            Console.Error.WriteLine(
                $"[Arius] {LevelEnvironmentVariable}='{rawValue}' is not a valid log level " +
                $"(expected one of {string.Join(", ", levelNames)}); defaulting to {DefaultLevelName}.");

        return DefaultLevelName;
    }
}
