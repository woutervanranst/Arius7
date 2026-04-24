using System.Collections.ObjectModel;

namespace Arius.E2E.Tests.Datasets;

internal sealed record SyntheticRepositoryState
{
    public SyntheticRepositoryState(IReadOnlyDictionary<string, string> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        Files = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(files, StringComparer.Ordinal));
    }

    public IReadOnlyDictionary<string, string> Files { get; }
}
