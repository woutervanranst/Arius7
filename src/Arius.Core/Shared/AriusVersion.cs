using System.Reflection;

namespace Arius.Core.Shared;

/// <summary>
/// The Arius build version, read once from this assembly's informational version.
/// Stamped at publish time from the git tag (<c>-p:Version</c>); falls back to "0.0.0" for
/// un-versioned local builds. Shared by the snapshot manifest and the web /api/info endpoint
/// so every surface reports the same value.
/// </summary>
public static class AriusVersion
{
    /// <summary>Full informational version, e.g. "0.0.51" (or "0.0.51+&lt;sha&gt;").</summary>
    public static string Informational { get; } =
        typeof(AriusVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? "0.0.0";

    /// <summary>Human-facing version with build metadata stripped, e.g. "0.0.51".</summary>
    public static string Display { get; } = Informational.Split('+', 2)[0];
}
