using Microsoft.Extensions.Configuration;

namespace Arius.Core.Shared.FileSystem;

/// <summary>
/// Configures which files and directories the archive enumeration skips.
/// It exists so the noise that should never be backed up (NAS metadata folders, OS junk files,
/// system/hidden entries) is defined in one central, configurable place instead of hardcoded.
/// </summary>
/// <remarks>
/// Bound via the options pattern in <c>AddArius</c>. The defaults live in Arius.Core's embedded
/// <c>appsettings.json</c> (see <see cref="EmbeddedDefaultConfiguration"/>), so every host — CLI, API,
/// Migration — inherits the same list and cannot drift. A host may override by passing its own
/// <see cref="IConfiguration"/> (an <c>Arius:Exclusions</c> section) to <c>AddArius</c>.
/// </remarks>
[SharedWithinAssembly] // bound by the DI composition root (ServiceCollectionExtensions) outside this namespace
internal sealed class FileExclusionOptions
{
    /// <summary>Configuration section that binds to this type.</summary>
    public const string SectionName = "Arius:Exclusions";

    /// <summary>Directory names whose entire subtree is excluded (case-insensitive).</summary>
    public List<string> ExcludedDirectoryNames { get; set; } = [];

    /// <summary>File names that are excluded (case-insensitive).</summary>
    public List<string> ExcludedFileNames { get; set; } = [];

    /// <summary>When <c>true</c>, files and directories carrying <see cref="FileAttributes.System"/> are excluded.</summary>
    public bool ExcludeSystemEntries { get; set; } = true;

    /// <summary>When <c>true</c>, files and directories carrying <see cref="FileAttributes.Hidden"/> are excluded.</summary>
    public bool ExcludeHiddenEntries { get; set; } = false;

    /// <summary>
    /// Builds an <see cref="IConfiguration"/> from Arius.Core's embedded <c>appsettings.json</c>.
    /// This is the central, drift-free source of default exclusions, used as the base layer of the
    /// options pipeline in <c>AddArius</c>.
    /// </summary>
    internal static IConfiguration EmbeddedDefaultConfiguration()
    {
        var assembly = typeof(FileExclusionOptions).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
                               .FirstOrDefault(n => n.EndsWith("appsettings.json", StringComparison.OrdinalIgnoreCase))
                           ?? throw new InvalidOperationException("Embedded appsettings.json resource not found.");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        return new ConfigurationBuilder().AddJsonStream(stream).Build();
    }
}
