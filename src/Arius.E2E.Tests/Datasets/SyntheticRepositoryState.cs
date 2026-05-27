using System.Collections.ObjectModel;
using Arius.Core.Shared.Hashes;

namespace Arius.E2E.Tests.Datasets;

internal sealed record SyntheticRepositoryState
{
    public SyntheticRepositoryState(LocalDirectory rootDirectory, IReadOnlyDictionary<RelativePath, ContentHash> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        RootDirectory = rootDirectory;
        Files = new ReadOnlyDictionary<RelativePath, ContentHash>(
            new Dictionary<RelativePath, ContentHash>(files));
    }

    public LocalDirectory RootDirectory { get; }

    public IReadOnlyDictionary<RelativePath, ContentHash> Files { get; }
}
