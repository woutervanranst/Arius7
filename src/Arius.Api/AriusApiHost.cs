using System.Text.Json;
using Arius.Api.AppData;
using Arius.Api.Composition;
using Arius.Api.Endpoints;
using Arius.Api.Hubs;
using Arius.AzureBlob;
using Arius.Core.Shared;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Arius.Api;

/// <summary>Shared composition for the Arius.Api pipeline, reused by the production host (Program.cs) and the
/// out-of-process Testing host (Arius.Api.Testing). The Core seam — <see cref="IRepositoryCoreComposer"/> — is
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

        builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysDir));
        builder.Services.AddSingleton(new AppDatabase(dbPath));
        builder.Services.AddSingleton<SecretProtector>();
        builder.Services.AddAzureBlobStorage();
        builder.Services.TryAddSingleton<IRepositoryCoreComposer, AzureRepositoryCoreComposer>();
        builder.Services.AddSingleton<RepositoryProviderRegistry>();
        builder.Services.AddSingleton<Arius.Api.Jobs.RestoreApprovalRegistry>();
        builder.Services.AddSingleton<Arius.Api.Jobs.JobStateRegistry>();
        builder.Services.AddSingleton<Arius.Api.Jobs.JobRunner>();
        builder.Services.AddHostedService<Arius.Api.Jobs.SchedulerService>();
        builder.Services.AddHostedService<Arius.Api.Jobs.RehydrationPollingService>();
        builder.Services.AddHostedService<Arius.Api.Jobs.StaleApprovalSweepService>();

        builder.Services.AddSignalR()
            .AddJsonProtocol(o => o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

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

        api.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        api.MapAccountEndpoints();
        api.MapRepositoryEndpoints();
        api.MapBrowseEndpoints();
        api.MapJobEndpoints();
        api.MapFilesystemEndpoints();
        app.MapHub<JobsHub>("/hubs/arius");
        app.MapFallbackToFile("index.html");

        return app;
    }
}
