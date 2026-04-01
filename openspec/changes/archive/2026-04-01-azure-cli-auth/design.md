## Context

The CLI authenticates to Azure Blob Storage exclusively via `StorageSharedKeyCredential` today. The single wiring point is `CliBuilder.BuildProductionServices` which constructs a `BlobServiceClient` from account name + key. The key is resolved via a 3-step chain: CLI flag → env var → user secrets. If no key is found, each verb prints an error and exits.

The `AzureBlobStorageService` receives a pre-authenticated `BlobContainerClient` and has no knowledge of credentials. `Arius.Core` has no Azure dependencies. This clean boundary means the entire auth change is scoped to `Arius.Cli`.

There is no global exception handling — `Program.cs` is 3 lines. Unhandled exceptions get System.CommandLine's default rendering (raw stack traces). The `FluentValidation` and `FluentResults` packages are referenced in `Arius.Core.csproj` but completely unused.

## Goals / Non-Goals

**Goals:**
- Allow users to authenticate via `az login` without needing an account key
- Detect auth and connectivity failures early (before pipeline starts) with clear error messages
- Add a global exception safety net with user-friendly rendering via Spectre.Console
- Remove dead dependencies (FluentValidation, FluentResults)

**Non-Goals:**
- Supporting `DefaultAzureCredential` or other credential types (managed identity, service principal, etc.) — `AzureCliCredential` only
- Changing the auth architecture in `Arius.Core` or `Arius.AzureBlob` — all changes stay in the CLI layer
- Implementing a `Result<T>` pattern to replace the existing `bool Success + string? ErrorMessage` result records
- Adding an explicit `--auth-mode` flag — auth mode is inferred from whether a key is available

## Decisions

### Decision 1: Credential resolution precedence

**Chosen**: Key sources first (all three), then `AzureCliCredential` as final fallback.

```
1. --key / -k CLI flag         → StorageSharedKeyCredential
2. ARIUS_KEY env var           → StorageSharedKeyCredential
3. User secrets                → StorageSharedKeyCredential
4. AzureCliCredential          → TokenCredential (from az login)
```

**Rationale**: Precedence follows specificity — explicit Arius-specific credentials (flag, env var, secrets) always win over an ambient Azure identity that may have nothing to do with Arius. A developer might have `az login` active for other Azure work while having a specific service account key in user secrets for Arius. The key should win.

This also means all three key sources produce the same credential type (`StorageSharedKeyCredential`), and the fallback to `AzureCliCredential` is a clean switch to a different auth mechanism — no mixing.

**Alternative considered**: `DefaultAzureCredential` which tries ~7 providers in sequence. Rejected because: slow first-token acquisition (2-5s), confusing aggregate error messages when all providers fail, and supports credential types (managed identity, environment credentials) that aren't relevant for a CLI tool run by a human.

### Decision 2: `AzureCliCredential` specifically (not `DefaultAzureCredential`)

**Chosen**: `new AzureCliCredential()` with no options.

**Rationale**: Targets exactly one scenario (user ran `az login`). Fast, predictable, clear error when `az` isn't available or not logged in. The error message from `CredentialUnavailableException` is straightforward vs. `DefaultAzureCredential`'s multi-page aggregate error.

### Decision 3: Preflight check on every invocation

**Chosen**: Always run a preflight check after constructing the `BlobServiceClient`, regardless of auth mechanism.

- **Archive** (`PreflightMode.ReadWrite`): Upload a small blob `.arius-preflight-probe` (empty content, `overwrite: true`), then delete it. Proves write + delete access.
- **Restore / Ls** (`PreflightMode.ReadOnly`): Call `container.ExistsAsync()`. Proves read access.

**Rationale**: Wrong account keys fail just as badly as missing RBAC roles. The preflight catches both. A single early check (one HTTP round-trip) prevents confusing mid-pipeline failures. The probe blob name `.arius-preflight-probe` is deterministic to avoid accumulation if the delete fails.

**Alternative considered**: Only preflight for token auth. Rejected because shared key auth can also fail (wrong key, revoked key, wrong account name). A universal check is simpler and more robust.

### Decision 4: Async factory signature with `PreflightMode`

**Chosen**: Change the factory delegate to:
```csharp
Func<string, string?, string?, string, PreflightMode, Task<IServiceProvider>>
//   account  key?    passphrase container  mode
```

`BuildProductionServices` becomes `async Task<IServiceProvider>`. The `PreflightMode` enum (`ReadOnly`, `ReadWrite`) tells the factory which probe to run. Each verb passes its mode.

**Rationale**: The factory already handles credential construction; preflight is a natural extension ("can I talk to storage?"). Keeping it inside the factory preserves the boundary — verbs don't need to know about `BlobContainerClient`, Azure SDK types, or credential details.

**Alternative considered**: Separate `PreflightCheck` method called from each verb after factory construction. Rejected because it would require exposing `BlobContainerClient` through DI and having verbs reference Azure SDK types, breaking the current clean separation where verbs only interact with `IServiceProvider`.

### Decision 5: `PreflightException` for known errors, global handler for unknown

**Chosen**: Two-tier exception handling:

1. `BuildProductionServices` catches known failures and throws `PreflightException(string userMessage, Exception inner)`:
   - `CredentialUnavailableException` → message listing all auth options (key sources + `az login`)
   - `RequestFailedException` with status 403 → RBAC guidance (varies by whether key or token was used)
   - `RequestFailedException` with status 404 → container not found message

2. Each verb catches `PreflightException` in its existing try block, prints `[red]Error:[/] {ex.Message}`, logs `Log.Error(ex, ...)`, returns exit code 1.

3. `Program.cs` gets a top-level `try/catch/finally`:
   - `catch (Exception ex)`: `AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything | ExceptionFormats.ShowLinks)` + `Log.Fatal(ex, "Unhandled exception")`
   - `finally`: `Log.CloseAndFlush()`

**Rationale**: Known errors get clean messages without stack traces. Unknown errors get a formatted (but complete) stack trace via Spectre.Console instead of a raw .NET dump. The audit log always gets flushed.

### Decision 6: Error messages by failure mode

| Auth | Failure | Message |
|------|---------|---------|
| Key | 403 | `Access denied. Verify the account key is correct for storage account '{name}'.` |
| Key | Other `RequestFailedException` | `Could not connect to storage account '{name}': {ex.Message}` |
| Token | `CredentialUnavailableException` | `No account key found and Azure CLI is not logged in.\n\nProvide a key via:\n  --key / -k\n  ARIUS_KEY environment variable\n  dotnet user-secrets\n\nOr log in via Azure CLI:\n  az login` |
| Token | 403 | `Authenticated via Azure CLI but access was denied on storage account '{name}'.\n\nAssign the required RBAC role:\n  Storage Blob Data Contributor  (for archive)\n  Storage Blob Data Reader       (for restore, ls)\n\n  az role assignment create --assignee <your-email> --role "Storage Blob Data Contributor" --scope /subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.Storage/storageAccounts/{name}` |
| Any | 404 (container) | `Container '{container}' not found on storage account '{name}'.` |

### Decision 7: Remove unused dependencies

Remove `FluentValidation` and `FluentResults` from `Directory.Packages.props` and `Arius.Core.csproj`. Neither has any `using` statements, classes, or calls in the codebase. They can be re-added if/when actually needed.

## Risks / Trade-offs

**[Preflight adds latency]** → One extra HTTP round-trip per invocation (~50-200ms). Acceptable for a CLI tool that then does minutes of work. The preflight only fires once at startup.

**[Probe blob left behind on delete failure]** → If `.arius-preflight-probe` upload succeeds but delete fails (network issue, timeout), a tiny empty blob remains in the container. Deterministic name means it won't accumulate. Next archive invocation will overwrite and re-delete it. No impact on Arius operations (the blob is outside the `chunks/`, `filetrees/`, etc. namespace).

**[RBAC role confusion]** → Users may struggle to assign the correct role, especially in corporate environments with restricted Azure AD permissions. Mitigated by including the exact `az role assignment create` command in the error message, but the user still needs sufficient AAD permissions to create role assignments.

**[`AzureCliCredential` token expiry]** → `az login` tokens expire (default 1 hour for access tokens, refreshable). For long-running archive operations, the Azure SDK handles token refresh automatically via the `TokenCredential` interface. However, if the refresh token itself expires (after days/weeks of no use), a mid-operation failure could occur. This is an inherent limitation of token-based auth and applies equally to any `TokenCredential` implementation.

**[Factory signature change breaks test mocks]** → The `serviceProviderFactory` delegate changes from `Func<string, string, string?, string, IServiceProvider>` to `Func<string, string?, string?, string, PreflightMode, Task<IServiceProvider>>`. All existing test code that provides this delegate must be updated. The change is mechanical but touches every test that builds a root command.
