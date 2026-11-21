using Arius.Api.FakeTestHost;
using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;

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
            services.AddSingleton<ScenarioGate>();   // required by ScriptedRepositoryCoreComposer
            services.RemoveAll<IRepositoryCoreComposer>();
            services.AddSingleton<IRepositoryCoreComposer, ScriptedRepositoryCoreComposer>();
        });
    }

    /// <summary>Seeds an account and a repository with protected secrets and returns the repository id.</summary>
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
            encryptedPassphrase: secrets.Protect("passphrase"));
    }

    /// <summary>An unstarted SignalR client for the in-process hub, using long polling over the test server.</summary>
    public HubConnection CreateHubConnection()
    {
        var handler = Server.CreateHandler();
        return new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/arius", o =>
            {
                o.HttpMessageHandlerFactory = _ => handler;
                o.Transports = HttpTransportType.LongPolling;
            })
            .Build();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;

        // On Windows a pooled SQLite connection keeps the database file and its -wal/-shm files open, so the
        // delete below would fail. The host is already torn down, so clearing the pools releases the last handles.
        SqliteConnection.ClearAllPools();

        TryDeleteDirectory(_root);
    }

    private static void TryDeleteDirectory(string path)
    {
        // On Windows a released SQLite handle can linger briefly: a poller can be mid-query at teardown, and the
        // synchronous Dispose does not await hosted-service shutdown. Retry briefly, then give up: a leaked temp
        // directory must not fail a passing test.
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
                // Report instead of swallowing: a persistent lock points at an unclosed handle, and leftovers
                // accumulate across CI runs.
                Console.Error.WriteLine($"[AriusApiFactory] could not clean up '{path}': {ex.Message}");
                return;
            }
        }
    }
}
