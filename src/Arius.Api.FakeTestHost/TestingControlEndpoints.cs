using Arius.Api.AppData;

namespace Arius.Api.FakeTestHost;

/// <summary>Out-of-process control surface for the hermetic Playwright suite — the runtime equivalent of the
/// in-process AriusApiFactory.SeedRepository + factory.Scenarios.Set*. Only mapped by the Arius.Api.FakeTestHost host,
/// so production never exposes it. Jobs still START through the real hub (StartArchive/StartRestore) — this only
/// seeds a repo, picks a named scenario, and releases gated runs.</summary>
public static class TestingControlEndpoints
{
    public static IEndpointRouteBuilder MapTestingControlEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/testing");

        g.MapPost("/reset", (AppDatabase db, ScenarioRegistry scenarios, ScenarioGate gate) =>
        {
            db.ResetAll();
            scenarios.Clear();
            gate.ReleaseAll();
            return Results.Ok();
        });

        g.MapPost("/seed-repo", (SeedRepoRequest req, AppDatabase db, SecretProtector secrets) =>
        {
            var dest = req.LocalPath ?? Path.Combine(Path.GetTempPath(), $"arius-e2e-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dest);
            var accountId = db.InsertAccount("e2e-account", secrets.Protect("e2e-key"));
            var repoId = db.InsertRepository(
                alias: req.Alias ?? "e2e",
                container: req.Container ?? "e2e-container",
                accountId: accountId,
                localPath: dest,
                defaultTier: req.DefaultTier ?? "Archive",
                encryptedPassphrase: secrets.Protect("passphrase"));
            return Results.Ok(new { repoId, localPath = dest });
        });

        g.MapPost("/scenario", (ScenarioRequest req, ScenarioRegistry scenarios) =>
        {
            switch (req.Name)
            {
                case "representativeArchive":
                    scenarios.SetArchive(req.RepoId, CanonicalScenarios.RepresentativeArchive(gated: req.Gated));
                    break;
                case "rehydratingRestore":
                    scenarios.SetRestore(req.RepoId, CanonicalScenarios.RehydratingRestore(gated: req.Gated));
                    break;
                case "rehydratingRestoreStaysPending":
                    scenarios.SetRestore(req.RepoId, CanonicalScenarios.RehydratingRestoreStaysPending());
                    break;
                case "onlineRestore":
                    scenarios.SetRestore(req.RepoId, CanonicalScenarios.OnlineRestore(gated: req.Gated));
                    break;
                default:
                    return Results.BadRequest(new { error = $"Unknown scenario '{req.Name}'." });
            }
            return Results.Ok();
        });

        g.MapPost("/release/{repoId:long}", (long repoId, ScenarioGate gate) => { gate.Release(repoId); return Results.Ok(); });

        return app;
    }

    private sealed record SeedRepoRequest(string? Alias, string? Container, string? LocalPath, string? DefaultTier);
    private sealed record ScenarioRequest(long RepoId, string Name, bool Gated = false);
}
