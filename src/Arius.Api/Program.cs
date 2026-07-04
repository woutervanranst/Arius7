using System.Text.Json;
using Arius.Api.AppData;
using Arius.Api.Composition;
using Arius.Api.Endpoints;
using Arius.Api.Hubs;
using Arius.AzureBlob;
using Arius.Core.Shared;
using Microsoft.AspNetCore.DataProtection;
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

    // ── Configuration: paths live on a mounted volume in Docker, a local folder in dev ──
    // NOTE: the dev folder is ".appstate" (not "data") because the source folder "AppData" and a
    // "data" runtime folder collide on case-insensitive filesystems (macOS/Windows).
    var dbPath = builder.Configuration["Arius:AppDbPath"]
                 ?? Path.Combine(builder.Environment.ContentRootPath, ".appstate", "arius-app.sqlite");
    var keysDir = builder.Configuration["Arius:DataProtectionKeysPath"]
                  ?? Path.Combine(Path.GetDirectoryName(dbPath)!, "keys");
    Directory.CreateDirectory(keysDir);

    // ── Services ──
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysDir));
    builder.Services.AddSingleton(new AppDatabase(dbPath));
    builder.Services.AddSingleton<SecretProtector>();
    builder.Services.AddAzureBlobStorage();
    builder.Services.AddSingleton<RepositoryProviderRegistry>();
    builder.Services.AddSingleton<Arius.Api.Jobs.RestoreApprovalRegistry>();
    builder.Services.AddSingleton<Arius.Api.Jobs.JobStateRegistry>();
    builder.Services.AddSingleton<Arius.Api.Jobs.JobRunner>();
    builder.Services.AddHostedService<Arius.Api.Jobs.SchedulerService>();

    builder.Services.AddSignalR()
        .AddJsonProtocol(o => o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

    builder.Services.AddCors(options => options.AddPolicy("web", policy =>
        policy.WithOrigins("http://localhost:4200")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials()
              .WithExposedHeaders("X-Arius-Version")));

    var app = builder.Build();

    app.UseCors("web");

    // Serve the built Angular SPA from wwwroot in production (no-op in dev, where ng serve is used).
    app.UseDefaultFiles();
    app.UseStaticFiles();

    // REST endpoints live under /api so they never collide with the Angular SPA's client-side
    // routes (/overview, /repos, /jobs, …). The SignalR hub lives under /hubs.
    var api = app.MapGroup("/api");

    // Stamp every API response with the running build version (the git tag of the deployed image)
    api.AddEndpointFilter(async (ctx, next) =>
    {
        ctx.HttpContext.Response.Headers["X-Arius-Version"] = AriusVersion.Display;
        return await next(ctx);
    });

    api.MapGet("/health", () => Results.Ok(new { status = "ok" }));
    api.MapAccountEndpoints();
    api.MapRepositoryEndpoints();
    api.MapBrowseEndpoints();
    api.MapJobEndpoints();
    api.MapFilesystemEndpoints();
    app.MapHub<JobsHub>("/hubs/arius");

    // SPA fallback: client-side routes (/overview, /repos/…) serve index.html (only when present).
    app.MapFallbackToFile("index.html");

    Log.Information("Arius.Api {Version} starting — app db {DbPath}", AriusVersion.Display, dbPath);
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
