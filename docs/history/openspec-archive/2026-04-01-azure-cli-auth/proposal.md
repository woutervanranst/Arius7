## Why

The CLI currently requires an Azure Storage account key for all operations. This forces users to manage and distribute shared keys, which is less secure and less convenient than identity-based authentication. Supporting `az login` (via `AzureCliCredential`) allows users to authenticate with their Azure AD identity — no keys to rotate, leak, or share. Additionally, the CLI has no structured error handling for authentication or connectivity failures, and two unused NuGet dependencies (FluentValidation, FluentResults) should be cleaned up.

## What Changes

- **Account key becomes optional**: When no key is found (CLI flag, env var, or user secrets), the CLI falls back to `AzureCliCredential` from `Azure.Identity` instead of erroring out.
- **Preflight connectivity check**: On every invocation (regardless of auth mechanism), the CLI performs a preflight check against Azure before starting the pipeline. For `archive`, this writes and deletes a probe blob (`.arius-preflight-probe`) to verify write access. For `restore` and `ls`, this calls `container.ExistsAsync()` to verify read access. Failures produce clear, actionable error messages (wrong key, not logged in, missing RBAC role).
- **Async service provider factory**: `BuildProductionServices` becomes async (`Task<IServiceProvider>`) to support the preflight check. The factory signature gains a `PreflightMode` enum parameter (`ReadOnly` or `ReadWrite`). All verb `Build()` methods and the `BuildRootCommand` factory delegate are updated accordingly.
- **Global exception handler**: `Program.cs` gains a top-level `try/catch/finally` that renders unhandled exceptions via `AnsiConsole.WriteException` (with `ShortenEverything | ShowLinks`), logs them as `Log.Fatal`, and ensures `Log.CloseAndFlush()` always runs.
- **`PreflightException` for known auth/connectivity errors**: A custom exception type thrown by `BuildProductionServices` when the preflight check fails. Verbs catch this specifically to show clean `[red]Error:[/]` messages without stack traces. Unknown exceptions propagate to the global handler.
- **`--key` option description updated**: From `"Azure Storage account key"` to `"Azure Storage account key (omit to use Azure CLI login)"`.
- **Remove unused dependencies**: FluentValidation and FluentResults are removed from `Directory.Packages.props` and `Arius.Core.csproj` (both are referenced but have zero usage in the codebase).

## Capabilities

### New Capabilities
- `azure-cli-auth`: Authentication fallback from account key to Azure CLI identity-based credentials, including preflight connectivity/authorization checks and user-friendly error messages for all auth failure modes.

### Modified Capabilities
- `cli`: The account key resolution requirement changes — key-not-found no longer errors but falls back to `AzureCliCredential`. The `--key` option description changes. The factory signature changes to async with a `PreflightMode` parameter. A global exception handler is added to `Program.cs`. Verbs catch `PreflightException` for clean error rendering.

## Impact

- **Packages**: Add `Azure.Identity` to `Directory.Packages.props` and `Arius.Cli.csproj`. Remove `FluentValidation` and `FluentResults` from `Directory.Packages.props` and `Arius.Core.csproj`.
- **CLI layer** (`Arius.Cli`): `Program.cs`, `CliBuilder.cs`, `ArchiveVerb.cs`, `RestoreVerb.cs`, `LsVerb.cs` all change. New `PreflightMode` enum and `PreflightException` class.
- **Core and AzureBlob**: No changes. The architecture boundary (Core knows nothing about Azure, AzureBlob knows nothing about credentials) is preserved.
- **Tests**: New tests for the parameter combination matrix (account/key presence x auth mechanism x preflight mode). Existing CLI tests update for the new async factory signature.
- **RBAC dependency**: Users authenticating via `az login` must have appropriate RBAC roles (`Storage Blob Data Contributor` for archive, `Storage Blob Data Reader` for restore/ls) assigned on the storage account. This is an Azure configuration requirement, not a code change.
