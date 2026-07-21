using Arius.Api;
using Arius.Api.Composition;
using Arius.Core.Shared;
using Serilog;

// Bootstrap logger — captures any failure during host build. AddAriusApi replaces Log.Logger with the one
// process-wide pipeline (repo-routed rolling file + console) once the app paths are known, and wires it to
// the host via UseSerilog. Both honor ARIUS_LOG_LEVEL (default Information).
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(AriusLogging.ResolveLevel())
    .WriteTo.Console()
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.AddAriusApi();
    var app = builder.Build();
    app.MapAriusApi();

    // Any job left "running" by a crash/restart is dead (its in-process run is gone) — reconciled to
    // "interrupted" inside AppDatabase's schema initializer (which ran when the AppDatabase singleton was
    // constructed in AddAriusApi), before the ux_jobs_one_active_per_repo unique index is created — so no
    // explicit call is needed here.

    Log.Information("Arius.Api {Version} starting", AriusVersion.Display);
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Arius.Api terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

// Exposed so Arius.Api.Integration.Tests can boot the app with WebApplicationFactory<Program>.
public partial class Program { }
