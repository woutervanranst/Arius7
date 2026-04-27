using System.Collections.ObjectModel;
using Arius.Core.Shared.Hashes;

namespace Arius.E2E.Tests.Datasets;

internal sealed record SyntheticRepositoryState
{
    public SyntheticRepositoryState(string rootPath, IReadOnlyDictionary<string, ContentHash> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(files);

        RootPath = rootPath;
        Files = new ReadOnlyDictionary<string, ContentHash>(
            new Dictionary<string, ContentHash>(files, StringComparer.Ordinal));
    }

    public string RootPath { get; }

    public IReadOnlyDictionary<string, ContentHash> Files { get; }
}
