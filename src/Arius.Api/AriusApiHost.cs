using System.Text.Json;
using Arius.Api.Features.Downloads;
using Arius.Api.Features.Restore;
using Arius.Api.Features.Schedules;
using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;
using Arius.Api.Shared.Downloads;
using Arius.Api.Shared.Hosting;
using Arius.Api.Shared.Hubs;
using Arius.Api.Shared.Jobs;
using Arius.AzureBlob;
using Arius.Core.Shared;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Scalar.AspNetCore;
using Serilog;

namespace Arius.Api;

/// <summary>Shared composition for the Arius.Api pipeline, reused by the production host (Program.cs) and the
/// out-of-process Testing host (Arius.Api.FakeTestHost). The Core seam — <see cref="IRepositoryCoreComposer"/> — is
/// registered with <c>TryAdd</c>, so a host that pre-registers a scripted composer wins without any environment
/// branch here (mirrors the CliHarness "inject the factory" pattern).</summary>
public static class AriusApiHost
{
    public static WebApplicationBuilder AddAriusApi(this WebApplicationBuilder builder)
    {
        var dbPath = builder.Configuration["Arius:AppDbPath"]
                     ?? Path.Combine(builder.Environment.ContentRootPath, ".appstate", "arius-app.sqlite");
        var keysDir = builder.Configuration["Arius:DataProtectionKeysPath"]
                      ?? Path.Combine(Path.GetDirectoryName(dbPath)!, "keys");
        Directory.CreateDirectory(keysDir);

        // The app-wide root logger for host/startup events (per-repository files are owned by the registry).
        // Also the static Log.Logger, so Program.cs's Log.Fatal/CloseAndFlush act on it; the host owns its
        // lifetime and flushes it on shutdown.
        var appLogDir  = Path.Combine(Path.GetDirectoryName(dbPath)!, "logs");
        var rootLogger = AriusLogging.BuildRootLogger(appLogDir);
        Log.Logger = rootLogger;
        builder.Host.UseSerilog(rootLogger, dispose: true);

        builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysDir));
        builder.Services.AddSingleton(new AppDatabase(dbPath));
        builder.Services.AddSingleton<SecretProtector>();
        builder.Services.AddSingleton(new DownloadStaging(Path.Combine(Path.GetDirectoryName(dbPath)!, "downloads")));
        builder.Services.AddAzureBlobStorage();
        builder.Services.TryAddSingleton<IRepositoryCoreComposer, AzureRepositoryCoreComposer>();
        builder.Services.AddSingleton(sp => new RepositoryProviderRegistry(
            sp.GetRequiredService<AppDatabase>(),
            sp.GetRequiredService<SecretProtector>(),
            sp.GetRequiredService<IRepositoryCoreComposer>(),
            sp.GetRequiredService<ILoggerFactory>()));
        builder.Services.AddSingleton<DownloadCleaner>();
        builder.Services.AddSingleton<RestoreApprovalRegistry>();
        builder.Services.AddSingleton<JobStateRegistry>();
        builder.Services.AddSingleton<JobRunner>();
        builder.Services.AddFeatures();
        builder.Services.AddHostedService<SchedulerService>();
        builder.Services.AddHostedService<RehydrationPollingService>();
        builder.Services.AddHostedService<StaleApprovalSweepService>();
        builder.Services.AddHostedService<DownloadCleanupService>();

        builder.Services.AddSignalR()
            .AddJsonProtocol(o => o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

        // OpenAPI document generation — Development only (mapped in MapAriusApi via Scalar).
        if (builder.Environment.IsDevelopment())
            builder.Services.AddOpenApi();

        builder.Services.AddCors(options => options.AddPolicy("web", policy =>
            policy.WithOrigins("http://localhost:4200")
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials()
                  .WithExposedHeaders("X-Arius-Version")));

        return builder;
    }

    public static WebApplication MapAriusApi(this WebApplication app)
    {
        app.UseCors("web");
        app.UseDefaultFiles();
        app.UseStaticFiles();

        var api = app.MapGroup("/api");
        api.AddEndpointFilter(async (ctx, next) =>
        {
            ctx.HttpContext.Response.Headers["X-Arius-Version"] = AriusVersion.Display;
            return await next(ctx);
        });

        api.MapEndpoints();
        app.MapHub<JobsHub>("/hubs/arius");

        // Development-only API docs: raw document at /openapi/v1.json, Scalar UI at /scalar.
        // Mapped as real routes so they resolve before the SPA fallback below.
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.MapScalarApiReference();
        }

        app.MapFallbackToFile("index.html");

        return app;
    }
}
