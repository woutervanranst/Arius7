using System.Collections.ObjectModel;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Hashes;

namespace Arius.E2E.Tests.Datasets;

internal sealed record SyntheticRepositoryState
{
    public SyntheticRepositoryState(LocalRootPath rootPath, IReadOnlyDictionary<RelativePath, ContentHash> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        RootPath = rootPath;
        Files = new ReadOnlyDictionary<RelativePath, ContentHash>(
            new Dictionary<RelativePath, ContentHash>(files));
    }

    public LocalRootPath RootPath { get; }

    public IReadOnlyDictionary<RelativePath, ContentHash> Files { get; }
}
