using Arius.Api.AppData;
using Arius.Api.Composition;
using Arius.Api.FakeTestHost;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Arius.Api.Integration.Tests.Harness;

/// <summary>Boots Arius.Api in-process with a throwaway SQLite app-db and a scripted Core.</summary>
public sealed class AriusApiFactory : WebApplicationFactory<Program>
{
    // A unique directory per test, not just a unique file name: AddAriusApi derives the app-wide log and
    // data-protection key directories from the db path, and parallel hosts must not share those.
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"arius-itest-{Guid.NewGuid():N}");
    private string DbPath => Path.Combine(_root, "arius-app.sqlite");

    public ScenarioRegistry Scenarios { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_root);
        builder.UseEnvironment("Testing");
        builder.UseSetting("Arius:AppDbPath", DbPath);
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
        // holds the .sqlite file (and its -wal/-shm sidecars) open, so the delete below throws
        // IOException("used by another process"). base.Dispose above has torn down the host (stopping
        // those pollers and closing the root/per-repo log files); clearing the Sqlite pool now releases the
        // last DB handles so the whole throwaway directory can be removed.
        SqliteConnection.ClearAllPools();

        TryDeleteDirectory(_root);
    }

    private static void TryDeleteDirectory(string path)
    {
        // Even after ClearAllPools, a just-released Sqlite handle can linger for a moment on Windows —
        // a background poller can be mid-query when the host is torn down, and WebApplicationFactory's
        // synchronous Dispose does not await hosted-service shutdown. Retry briefly, then give up: a
        // leaked throwaway temp directory must never fail an otherwise-passing test.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20)
            {
                Thread.Sleep(50);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Report rather than swallow: silence here would hide exactly the file-locking regression
                // this retry loop exists to absorb (a handle we forgot to close), and the leftovers
                // accumulate across CI runs.
                Console.Error.WriteLine($"[AriusApiFactory] could not clean up '{path}': {ex.Message}");
                return;
            }
        }
    }
}
