using System.Collections.ObjectModel;

namespace Arius.E2E.Tests.Datasets;

internal sealed record SyntheticRepositoryState
{
    public SyntheticRepositoryState(string rootPath, IReadOnlyDictionary<string, string> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(files);

        RootPath = rootPath;
        Files = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(files, StringComparer.Ordinal));
    }

    public string RootPath { get; }

    public IReadOnlyDictionary<string, string> Files { get; }
}
