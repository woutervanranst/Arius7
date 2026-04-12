using Arius.Explorer.Settings;

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
