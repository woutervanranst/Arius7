using Arius.Api.Features.Cost;
using Microsoft.Data.Sqlite;

namespace Arius.Api.Shared.AppData;

public sealed partial class AppDatabase
{
    private const string AutoAcceptBelowKey = "cost.autoAcceptBelow";

    /// <summary>The server-wide cost confirmation setting; an absent row falls back to <see cref="CostSettings.Default"/>.</summary>
    public CostSettings GetCostSettings()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", AutoAcceptBelowKey);
        return command.ExecuteScalar() is string value
            ? new CostSettings(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
            : CostSettings.Default;
    }

    public void SaveCostSettings(CostSettings settings)
    {
        using var connection = OpenConnection();
        using var upsert = connection.CreateCommand();
        upsert.CommandText = "INSERT INTO app_settings(key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        upsert.Parameters.AddWithValue("$key", AutoAcceptBelowKey);
        upsert.Parameters.AddWithValue("$value", settings.AutoAcceptBelow.ToString(System.Globalization.CultureInfo.InvariantCulture));
        upsert.ExecuteNonQuery();
    }
}
