using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;
using Arius.Api;
using Arius.Core.Shared;
using Serilog;

// Bootstrap logger for failures during host build; AddAriusApi replaces it with the root logger once the log
// directory is known.
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

// Public so the integration tests can boot the app with WebApplicationFactory<Program>.
public partial class Program { }
