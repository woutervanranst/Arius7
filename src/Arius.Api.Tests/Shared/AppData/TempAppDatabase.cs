using Arius.Api.Shared.AppData;
using Microsoft.Data.Sqlite;

namespace Arius.Api.Tests.Shared.AppData;

/// <summary>An <see cref="AppDatabase"/> in a throw-away temp directory, released and deleted on dispose.</summary>
internal sealed class TempAppDatabase : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"arius-api-tests-{Guid.NewGuid():N}");

    public TempAppDatabase() => Database = new AppDatabase(Path.Combine(_directory, "app.db"));

    public AppDatabase Database { get; }

    public long AddAccount(string name = "acc") => Database.InsertAccount(name, encryptedAccountKey: null);

    public long AddRepository(long accountId, string alias = "alias", string container = "container") =>
        Database.InsertRepository(alias, container, accountId, localPath: null, encryptedPassphrase: null);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
