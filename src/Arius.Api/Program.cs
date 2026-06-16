using Arius.Api.Composition;
using Arius.Api.AppData;
using Arius.Api.Endpoints;
using Arius.Api.Hubs;
using Arius.AzureBlob;
using Arius.Core.Shared.Storage;
using Microsoft.AspNetCore.DataProtection;
using Serilog;
using System.Text.Json;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
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
    builder.Services.AddSingleton<IBlobServiceFactory, AzureBlobServiceFactory>();
    builder.Services.AddSingleton<RepositoryProviderRegistry>();
    builder.Services.AddSingleton<Arius.Api.Jobs.RestoreApprovalRegistry>();
    builder.Services.AddSingleton<Arius.Api.Jobs.JobRunner>();

    builder.Services.AddSignalR()
        .AddJsonProtocol(o => o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

    builder.Services.AddCors(options => options.AddPolicy("web", policy =>
        policy.WithOrigins("http://localhost:4200")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials()));

    var app = builder.Build();

    app.UseCors("web");

    // REST endpoints live under /api so they never collide with the Angular SPA's client-side
    // routes (/overview, /repos, /jobs, …). The SignalR hub lives under /hubs.
    var api = app.MapGroup("/api");
    api.MapGet("/health", () => Results.Ok(new { status = "ok" }));
    api.MapAccountEndpoints();
    api.MapRepositoryEndpoints();
    api.MapBrowseEndpoints();
    app.MapHub<JobsHub>("/hubs/arius");

    Log.Information("Arius.Api starting — app db {DbPath}", dbPath);
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
