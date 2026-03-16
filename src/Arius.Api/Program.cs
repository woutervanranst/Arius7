using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arius.Azure;
using Arius.Core.Application.Backup;
using Arius.Core.Application.Check;
using Arius.Core.Application.CostEstimate;
using Arius.Core.Application.Diff;
using Arius.Core.Application.Find;
using Arius.Core.Application.Forget;
using Arius.Core.Application.Init;
using Arius.Core.Application.Ls;
using Arius.Core.Application.Prune;
using Arius.Core.Application.Repair;
using Arius.Core.Application.Restore;
using Arius.Core.Application.Snapshots;
using Arius.Core.Application.Stats;
using Arius.Core.Application.Tag;
using Arius.Core.Infrastructure;
using Arius.Core.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

// ─── Build ───────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

// ─── Read config (env vars → user secrets in Development) ────────────────────
// Priority (highest → lowest): environment variables, user secrets, appsettings.
// In production only environment variables are active; user secrets are a
// Development-only convenience that never ships in a container image.

var connStr   = builder.Configuration["ARIUS_REPOSITORY"]
             ?? throw new InvalidOperationException(
                    "ARIUS_REPOSITORY is not set. " +
                    "Supply it as an environment variable or, in Development, via 'dotnet user-secrets set ARIUS_REPOSITORY <value>'.");
var container = builder.Configuration["ARIUS_CONTAINER"]
             ?? throw new InvalidOperationException(
                    "ARIUS_CONTAINER is not set. " +
                    "Supply it as an environment variable or, in Development, via 'dotnet user-secrets set ARIUS_CONTAINER <value>'.");
var password  = builder.Configuration["ARIUS_PASSWORD"]
             ?? throw new InvalidOperationException(
                    "ARIUS_PASSWORD is not set. " +
                    "Supply it as an environment variable or, in Development, via 'dotnet user-secrets set ARIUS_PASSWORD <value>'.");

builder.Services.ConfigureHttpJsonOptions(opts =>
{
    opts.SerializerOptions.PropertyNamingPolicy   = JsonNamingPolicy.CamelCase;
    opts.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    opts.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddSignalR()
    .AddJsonProtocol(opts =>
    {
        opts.PayloadSerializerOptions.PropertyNamingPolicy   = JsonNamingPolicy.CamelCase;
        opts.PayloadSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        opts.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

// Allow web UI in development
builder.Services.AddCors(opts =>
    opts.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

// ─── Handler registrations ────────────────────────────────────────────────────

Func<string, string, AzureRepository> repoFactory =
    (cs, c) => new AzureRepository(new AzureBlobStorageProvider(cs, c));

builder.Services.AddSingleton(repoFactory);
builder.Services.AddSingleton<InitHandler>();
builder.Services.AddSingleton<BackupHandler>();
builder.Services.AddSingleton<RestoreHandler>();
builder.Services.AddSingleton<SnapshotsHandler>();
builder.Services.AddSingleton<LsHandler>();
builder.Services.AddSingleton<FindHandler>();
builder.Services.AddSingleton<ForgetHandler>();
builder.Services.AddSingleton<PruneHandler>();
builder.Services.AddSingleton<CheckHandler>();
builder.Services.AddSingleton<DiffHandler>();
builder.Services.AddSingleton<StatsHandler>();
builder.Services.AddSingleton<TagHandler>();
builder.Services.AddSingleton<RepairHandler>();
builder.Services.AddSingleton<CostEstimateHandler>();

// In-flight operations tracker
builder.Services.AddSingleton<OperationTracker>();

// ─── App ──────────────────────────────────────────────────────────────────────

var app = builder.Build();

app.UseCors();
app.UseStaticFiles();                     // 13.2: serve Vue wwwroot
app.MapHub<OperationsHub>("/hub/operations");

// ─── GET /api/snapshots ───────────────────────────────────────────────────────

app.MapGet("/api/snapshots", (SnapshotsHandler handler, CancellationToken ct) =>
    StreamResponse(handler.Handle(new ListSnapshotsRequest(connStr, container, password), ct)));

// ─── GET /api/snapshots/{id} ──────────────────────────────────────────────────

app.MapGet("/api/snapshots/{id}", async (string id, SnapshotsHandler handler, CancellationToken ct) =>
{
    await foreach (var s in handler.Handle(new ListSnapshotsRequest(connStr, container, password), ct))
    {
        if (s.Id.Value.StartsWith(id, StringComparison.OrdinalIgnoreCase))
            return Results.Ok(s);
    }
    return Results.NotFound();
});

// ─── GET /api/snapshots/{id}/tree ─────────────────────────────────────────────

app.MapGet("/api/snapshots/{id}/tree", (
    string id,
    LsHandler handler,
    [FromQuery] string? path,
    [FromQuery] bool recursive,
    CancellationToken ct) =>
    StreamResponse(handler.Handle(
        new LsRequest(connStr, container, password, id, path ?? "/", recursive), ct)));

// ─── GET /api/snapshots/{id}/find ─────────────────────────────────────────────

app.MapGet("/api/snapshots/{id}/find", (
    string id,
    FindHandler handler,
    [FromQuery] string pattern,
    [FromQuery] string? pathPrefix,
    CancellationToken ct) =>
    StreamResponse(handler.Handle(
        new FindRequest(connStr, container, password, pattern, id, pathPrefix), ct)));

// ─── POST /api/backup ─────────────────────────────────────────────────────────

app.MapPost("/api/backup", async (
    BackupStartBody body,
    BackupHandler handler,
    OperationTracker tracker,
    IHubContext<OperationsHub> hub,
    CancellationToken ct) =>
{
    var opId = tracker.Start();
    _ = Task.Run(async () =>
    {
        try
        {
            var parallelismOpts = body.Parallelism is > 0
                ? new ParallelismOptions(body.Parallelism.Value, 0, 0, 0, 0)
                : null;

            await foreach (var ev in handler.Handle(
                new BackupRequest(connStr, container, password, body.Paths ?? [], Parallelism: parallelismOpts), tracker.TokenFor(opId)))
            {
                await hub.Clients.All.SendAsync("BackupEvent", opId, ev);
            }
        }
        catch (OperationCanceledException) { /* cancelled */ }
        catch (Exception ex) { await hub.Clients.All.SendAsync("Error", opId, ex.Message); }
        finally { tracker.Complete(opId); }
    }, ct);

    return Results.Accepted($"/api/operations/{opId}", new { operationId = opId });
});

// ─── POST /api/restore ────────────────────────────────────────────────────────

app.MapPost("/api/restore", async (
    RestoreStartBody body,
    RestoreHandler handler,
    OperationTracker tracker,
    IHubContext<OperationsHub> hub,
    CancellationToken ct) =>
{
    if (string.IsNullOrEmpty(body.SnapshotId) || string.IsNullOrEmpty(body.TargetPath))
        return Results.BadRequest("snapshotId and targetPath are required");

    var opId = tracker.Start();
    _ = Task.Run(async () =>
    {
        try
        {
            var parallelismOpts = body.Parallelism is > 0
                ? new ParallelismOptions(0, 0, 0, body.Parallelism.Value, body.Parallelism.Value)
                : null;

            await foreach (var ev in handler.Handle(
                new RestoreRequest(connStr, container, password,
                    body.SnapshotId, body.TargetPath, body.Include,
                    Parallelism: parallelismOpts, TempPath: body.TempPath), tracker.TokenFor(opId)))
            {
                await hub.Clients.All.SendAsync("RestoreEvent", opId, ev);
            }
        }
        catch (OperationCanceledException) { /* cancelled */ }
        catch (Exception ex) { await hub.Clients.All.SendAsync("Error", opId, ex.Message); }
        finally { tracker.Complete(opId); }
    }, ct);

    return Results.Accepted($"/api/operations/{opId}", new { operationId = opId });
});

// ─── POST /api/forget ─────────────────────────────────────────────────────────

app.MapPost("/api/forget", (
    ForgetStartBody body,
    ForgetHandler handler,
    CancellationToken ct) =>
    StreamResponse(handler.Handle(
        new ForgetRequest(connStr, container, password,
            body.Policy ?? new RetentionPolicy(),
            body.DryRun), ct)));

// ─── POST /api/prune ──────────────────────────────────────────────────────────

app.MapPost("/api/prune", async (
    PruneStartBody body,
    PruneHandler handler,
    OperationTracker tracker,
    IHubContext<OperationsHub> hub,
    CancellationToken ct) =>
{
    var opId = tracker.Start();
    _ = Task.Run(async () =>
    {
        try
        {
            await foreach (var ev in handler.Handle(
                new PruneRequest(connStr, container, password, body.DryRun), tracker.TokenFor(opId)))
            {
                await hub.Clients.All.SendAsync("PruneEvent", opId, ev);
            }
        }
        catch (OperationCanceledException) { /* cancelled */ }
        catch (Exception ex) { await hub.Clients.All.SendAsync("Error", opId, ex.Message); }
        finally { tracker.Complete(opId); }
    }, ct);

    return Results.Accepted($"/api/operations/{opId}", new { operationId = opId });
});

// ─── GET /api/stats ───────────────────────────────────────────────────────────

app.MapGet("/api/stats", async (StatsHandler handler, CancellationToken ct) =>
    Results.Ok(await handler.Handle(new StatsRequest(connStr, container, password), ct)));

// ─── GET /api/diff/{snap1}/{snap2} ────────────────────────────────────────────

app.MapGet("/api/diff/{snap1}/{snap2}", (
    string snap1,
    string snap2,
    DiffHandler handler,
    CancellationToken ct) =>
    StreamResponse(handler.Handle(
        new DiffRequest(connStr, container, password, snap1, snap2), ct)));

// ─── DELETE /api/operations/{id} ─────────────────────────────────────────────

app.MapDelete("/api/operations/{id}", (string id, OperationTracker tracker) =>
{
    tracker.Cancel(id);
    return Results.NoContent();
});

// ─── Fallback: SPA index.html (for Vue router) ────────────────────────────────

app.MapFallbackToFile("index.html");

app.Run();

// ─── Helpers ─────────────────────────────────────────────────────────────────

/// <summary>
/// Writes each item from an <see cref="IAsyncEnumerable{T}"/> as a JSON array
/// (newline-delimited) to a streaming response.
/// </summary>
static IResult StreamResponse<T>(IAsyncEnumerable<T> items)
    => Results.Ok(items);       // ASP.NET Core 9+ supports IAsyncEnumerable directly

// ─── Request / Response bodies ───────────────────────────────────────────────

record BackupStartBody(IReadOnlyList<string>? Paths, int? Parallelism = null);
record RestoreStartBody(string? SnapshotId, string? TargetPath, string? Include, int? Parallelism = null, string? TempPath = null);
record ForgetStartBody(RetentionPolicy? Policy, bool DryRun = false);
record PruneStartBody(bool DryRun = false);

// ─── Operation tracker ───────────────────────────────────────────────────────

public sealed class OperationTracker
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _ops = new();

    public string Start()
    {
        var id  = Guid.NewGuid().ToString("N")[..12];
        var cts = new CancellationTokenSource();
        _ops[id] = cts;
        return id;
    }

    public CancellationToken TokenFor(string id)
        => _ops.TryGetValue(id, out var cts) ? cts.Token : CancellationToken.None;

    public void Cancel(string id)
    {
        if (_ops.TryGetValue(id, out var cts))
            cts.Cancel();
    }

    public void Complete(string id)
        => _ops.TryRemove(id, out _);
}

// ─── SignalR Hub ──────────────────────────────────────────────────────────────

public sealed class OperationsHub : Hub
{
    // Clients subscribe to operations by operation ID.
    // The server pushes events from BackupHandler, RestoreHandler, PruneHandler, ForgetHandler
    // via IHubContext<OperationsHub> from the background tasks above.

    public async Task Subscribe(string operationId)
        => await Groups.AddToGroupAsync(Context.ConnectionId, $"op-{operationId}");

    public async Task Unsubscribe(string operationId)
        => await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"op-{operationId}");
}
