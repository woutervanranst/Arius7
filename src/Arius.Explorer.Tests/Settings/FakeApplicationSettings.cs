using System.Collections.ObjectModel;
using Arius.Explorer.Settings;

namespace Arius.Explorer.Tests.Settings;

internal sealed class FakeApplicationSettings : IApplicationSettings
{
    public ObservableCollection<RepositoryOptions> RecentRepositories { get; } = [];
    public int RecentLimit { get; set; } = 10;
    public int SaveCalls { get; private set; }

    public void Save() => SaveCalls++;
}
