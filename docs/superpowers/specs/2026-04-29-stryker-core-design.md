# Stryker Core Mutation Testing Design

## Context

Arius is a .NET 10 solution with central package management and TUnit-based test projects. CI already runs unit and integration tests and collects coverage with `dotnet-coverage` on supported runners. The repository does not currently have a Stryker mutation testing setup.

The first Stryker setup should target `Arius.Core` only. `Arius.Core` contains the core archive, restore, list, snapshot, chunk, and filetree behavior where mutation testing is likely to give the best early signal. Integration and E2E tests are intentionally excluded from the first setup because they can depend on Docker, Azurite, live Azure credentials, or longer-running workflows.

## Goals

- Add a minimal Stryker.NET configuration for mutation testing `src/Arius.Core/Arius.Core.csproj`.
- Use `src/Arius.Core.Tests/Arius.Core.Tests.csproj` as the initial test project.
- Keep the setup local/manual at first, without enforcing a CI threshold.
- Document how to install and run Stryker for developers who have not used it before.

## Non-Goals

- Do not mutate `Arius.Cli`, `Arius.AzureBlob`, `Arius.Explorer`, or benchmark code in the first setup.
- Do not run integration or E2E tests through Stryker initially.
- Do not add a mutation score quality gate to CI until the baseline score and runtime are known.
- Do not tune broad exclusions or thresholds before seeing the first report.

## Design

Add one repository-level Stryker configuration file that points Stryker at the Core project and its Core test project. The configuration should generate an HTML report so the initial output is easy to inspect locally.

The expected developer workflow is:

```bash
dotnet tool install --global dotnet-stryker
dotnet stryker --config-file stryker-config.json
```

The README development section should mention that this is a local, exploratory mutation testing command for `Arius.Core`, not a CI requirement yet. The documentation should also warn that Stryker runs can be slower than normal unit tests and that the first useful outcome is a baseline report.

## Testing And Verification

Verification for the setup is:

- Restore/build still succeeds for the affected projects.
- `dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj` passes.
- `dotnet stryker --config-file stryker-config.json` starts successfully and produces a mutation report for `Arius.Core`.

If the first Stryker run exposes incompatible defaults with TUnit or the current .NET SDK, adjust only the minimum config needed to get the Core-only run working.

## Future Work

After the first baseline is understood, consider:

- Adding a manually triggered CI workflow for Stryker.
- Extending mutation testing to `Arius.Cli` or `Arius.AzureBlob`.
- Introducing thresholds only after mutation score and runtime are acceptable.
