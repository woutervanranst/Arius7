using Arius.Api.Composition;
using Arius.Api.Data;
using Arius.Api.Endpoints;
using Arius.AzureBlob;
using Arius.Core.Shared.Storage;
using Microsoft.AspNetCore.DataProtection;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    // ── Configuration: paths live on a mounted volume in Docker, a local folder in dev ──
    var dbPath = builder.Configuration["Arius:AppDbPath"]
                 ?? Path.Combine(builder.Environment.ContentRootPath, "data", "arius-app.sqlite");
    var keysDir = builder.Configuration["Arius:DataProtectionKeysPath"]
                  ?? Path.Combine(Path.GetDirectoryName(dbPath)!, "keys");
    Directory.CreateDirectory(keysDir);

    // ── Services ──
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysDir));
    builder.Services.AddSingleton(new AppDatabase(dbPath));
    builder.Services.AddSingleton<SecretProtector>();
    builder.Services.AddSingleton<IBlobServiceFactory, AzureBlobServiceFactory>();
    builder.Services.AddSingleton<RepositoryProviderRegistry>();

    builder.Services.AddCors(options => options.AddPolicy("web", policy =>
        policy.WithOrigins("http://localhost:4200")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials()));

    var app = builder.Build();

    app.UseCors("web");

    app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
    app.MapAccountEndpoints();
    app.MapRepositoryEndpoints();

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
