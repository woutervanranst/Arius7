using Arius.Api.AppData;
using Arius.Api.Composition;
using Arius.Api.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Arius.Api.Integration.Tests.Harness;

/// <summary>Boots Arius.Api in-process with a throwaway SQLite app-db and a scripted Core.</summary>
public sealed class AriusApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"arius-itest-{Guid.NewGuid():N}.sqlite");

    public ScenarioRegistry Scenarios { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Arius:AppDbPath", _dbPath);
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(Scenarios);
            services.AddSingleton<ScenarioGate>();   // ScriptedRepositoryCoreComposer ctor dependency
            services.RemoveAll<IRepositoryCoreComposer>();
            services.AddSingleton<IRepositoryCoreComposer, ScriptedRepositoryCoreComposer>();
        });
    }

    /// <summary>Seeds an account + repository row (protected secrets, no real Azure) and returns the repo id.</summary>
    public long SeedRepository(string? localPath = null)
    {
        var db      = Services.GetRequiredService<AppDatabase>();
        var secrets = Services.GetRequiredService<SecretProtector>();
        var accountId = db.InsertAccount("fake-account", secrets.Protect("fake-key"));
        return db.InsertRepository(
            alias: "itest",
            container: "itest-container",
            accountId: accountId,
            localPath: localPath ?? Path.Combine(Path.GetTempPath(), $"arius-itest-src-{Guid.NewGuid():N}"),
            defaultTier: "Archive",
            encryptedPassphrase: secrets.Protect("passphrase"));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;

        // AppDatabase opens pooled connections (Pooling=true) and runs in WAL mode, and the background
        // job pollers keep the pool warm right up to shutdown. On Windows a pooled physical connection
        // holds the .sqlite file (and its -wal/-shm sidecars) open, so the deletes below throw
        // IOException("used by another process"). base.Dispose above has torn down the host (stopping
        // those pollers); clearing the Sqlite pool now releases the last handles so the throwaway files
        // can be removed.
        SqliteConnection.ClearAllPools();

        TryDelete(_dbPath);
        TryDelete(_dbPath + "-wal");
        TryDelete(_dbPath + "-shm");
    }

    private static void TryDelete(string path)
    {
        // Even after ClearAllPools, a just-released Sqlite handle can linger for a moment on Windows —
        // a background poller can be mid-query when the host is torn down, and WebApplicationFactory's
        // synchronous Dispose does not await hosted-service shutdown. Retry briefly, then give up: a
        // leaked throwaway temp file must never fail an otherwise-passing test.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(50);
            }
            catch (IOException)
            {
                return;
            }
        }
    }
}
