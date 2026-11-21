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

/// <summary>Composes the Arius.Api pipeline for both the production host and the test host (Arius.Api.FakeTestHost).
/// <see cref="IRepositoryCoreComposer"/> is registered with <c>TryAdd</c>, so a host that registers its own composer
/// first replaces the Azure one.</summary>
public static class AriusApiHost
{
    public static WebApplicationBuilder AddAriusApi(this WebApplicationBuilder builder)
    {
        var dbPath = builder.Configuration["Arius:AppDbPath"]
                     ?? Path.Combine(builder.Environment.ContentRootPath, ".appstate", "arius-app.sqlite");
        var keysDir = builder.Configuration["Arius:DataProtectionKeysPath"]
                      ?? Path.Combine(Path.GetDirectoryName(dbPath)!, "keys");
        Directory.CreateDirectory(keysDir);

        // Root logger for host events; per-repository logs are owned by the registry. Also assigned to the static
        // Log.Logger, so Program.cs's Log.Fatal and CloseAndFlush use it.
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

        // Development only: the OpenAPI document at /openapi/v1.json and the Scalar UI at /scalar.
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.MapScalarApiReference();
        }

        app.MapFallbackToFile("index.html");

        return app;
    }
}
