using Microsoft.Data.Sqlite;

namespace Arius.Core.Tests;

internal static class SqliteTestSessionHooks
{
    [After(TestSession)]
    public static void ClearSqlitePools()
    {
        SqliteConnection.ClearAllPools();
    }
}
