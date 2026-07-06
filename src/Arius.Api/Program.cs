using Arius.Api;
using Arius.Core.Shared;
using Serilog;
using Serilog.Events;

// Global log level: ARIUS_LOG_LEVEL (Verbose/Debug/Information/Warning/Error/Fatal); default Information.
var logLevel = Enum.TryParse<LogEventLevel>(Environment.GetEnvironmentVariable("ARIUS_LOG_LEVEL")?.Trim(), ignoreCase: true, out var parsed) ? parsed : LogEventLevel.Information;
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(logLevel)
    .WriteTo.Console()
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

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
