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

    [Test]
    public void RecentRepositories_WhenUnset_ReturnsReusableCollection()
    {
        var settings = new ApplicationSettings();

        var first = settings.RecentRepositories;
        var second = settings.RecentRepositories;

        first.ShouldNotBeNull();
        second.ShouldBeSameAs(first);
    }

    [Test]
    public void UpgradeRequired_WhenSetFalse_ReturnsFalse()
    {
        var settings = new ApplicationSettings
        {
            UpgradeRequired = false,
        };

        settings.UpgradeRequired.ShouldBeFalse();
    }

}
