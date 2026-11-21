using System.Globalization;
using Arius.Api.Shared.AppData;
using Shouldly;

namespace Arius.Api.Tests.Shared.AppData;

public sealed class CostSettingsStoreTests
{
    private static AppDatabase NewDatabase() =>
        new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"arius-api-tests-{Guid.NewGuid():N}.db"));

    [Test]
    public async Task Cost_settings_default_to_auto_accepting_below_one_cent_and_round_trip()
    {
        var db = NewDatabase();

        db.GetCostSettings().ShouldBe(CostSettings.Default);
        CostSettings.Default.ShouldBe(new CostSettings(0.01m));

        db.SaveCostSettings(new CostSettings(0.25m));

        db.GetCostSettings().ShouldBe(new CostSettings(0.25m));
    }

    [Test]
    public async Task Cost_settings_saved_under_a_comma_decimal_culture_read_back_under_another()
    {
        var db = NewDatabase();
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("nl-BE");
            db.SaveCostSettings(new CostSettings(0.25m));

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            db.GetCostSettings().ShouldBe(new CostSettings(0.25m));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
