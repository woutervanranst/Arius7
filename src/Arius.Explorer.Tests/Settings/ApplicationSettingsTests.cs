using Arius.Explorer.Settings;
using Shouldly;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Arius.Explorer.Tests.Settings;

public class ApplicationSettingsTests
{
    [Test]
    public void ApplicationSettings_Defaults_AreReasonable()
    {
        var settings = new ApplicationSettings();

        settings.RecentRepositories.ShouldNotBeNull();
        settings.RecentLimit.ShouldBe(10);
        settings.UpgradeRequired.ShouldBeTrue();
    }

}
