using Arius.Core.Shared.FileSystem;

namespace Arius.Core.Features.ArchiveCommand;

/// <summary>
/// Decides whether a file or directory should be excluded from the archive, based on a
/// <see cref="FileExclusionOptions"/> set of name lists and attribute toggles.
/// It exists to keep the exclusion policy in one place, applied during enumeration so excluded
/// entries never enter a snapshot.
/// </summary>
[SharedWithinAssembly] // constructed by the DI composition root (ServiceCollectionExtensions) outside this namespace
internal sealed class FileExclusionFilter
{
    private readonly HashSet<string> _excludedDirectoryNames;
    private readonly HashSet<string> _excludedFileNames;
    private readonly bool            _excludeSystem;
    private readonly bool            _excludeHidden;

    public FileExclusionFilter(FileExclusionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _excludedDirectoryNames = new HashSet<string>(options.ExcludedDirectoryNames, StringComparer.OrdinalIgnoreCase);
        _excludedFileNames      = new HashSet<string>(options.ExcludedFileNames,      StringComparer.OrdinalIgnoreCase);
        _excludeSystem          = options.ExcludeSystemEntries;
        _excludeHidden          = options.ExcludeHiddenEntries;
    }

    /// <summary>
    /// A filter that excludes nothing. Used where no exclusion policy is supplied (e.g. tests).
    /// </summary>
    public static FileExclusionFilter None { get; } = new(new FileExclusionOptions
    {
        ExcludeSystemEntries = false,
        ExcludeHiddenEntries = false,
    });

    /// <summary>
    /// True when an attribute rule is active. Lets the enumerator avoid statting an entry's
    /// attributes when only name-based rules apply.
    /// </summary>
    public bool RequiresAttributes => _excludeSystem || _excludeHidden;

    public bool ShouldExcludeDirectory(PathSegment name, FileAttributes attributes) =>
        _excludedDirectoryNames.Contains(name.ToString()) || MatchesAttributes(attributes);

    public bool ShouldExcludeFile(PathSegment name, FileAttributes attributes) =>
        _excludedFileNames.Contains(name.ToString()) || MatchesAttributes(attributes);

    private bool MatchesAttributes(FileAttributes attributes) =>
        (_excludeSystem && attributes.HasFlag(FileAttributes.System)) ||
        (_excludeHidden && attributes.HasFlag(FileAttributes.Hidden));
}
