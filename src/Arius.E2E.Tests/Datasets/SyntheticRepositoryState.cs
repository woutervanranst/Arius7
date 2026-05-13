using System.Collections.ObjectModel;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Hashes;

namespace Arius.E2E.Tests.Datasets;

internal sealed record SyntheticRepositoryState
{
    public SyntheticRepositoryState(string rootPath, IReadOnlyDictionary<RelativePath, ContentHash> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(files);

        RootPath = rootPath;
        Files = new ReadOnlyDictionary<RelativePath, ContentHash>(
            new Dictionary<RelativePath, ContentHash>(files));
    }

    public string RootPath { get; }

    public IReadOnlyDictionary<RelativePath, ContentHash> Files { get; }
}
