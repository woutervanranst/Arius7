## 1. Remove unused dependencies

- [ ] 1.1 Remove `FluentValidation` and `FluentResults` package references from `src/Arius.Core/Arius.Core.csproj`
- [ ] 1.2 Remove `FluentValidation` and `FluentResults` entries from `src/Directory.Packages.props`
- [ ] 1.3 Verify `dotnet build` succeeds after removal

## 2. Add Azure.Identity dependency

- [ ] 2.1 Add `Azure.Identity` to `src/Directory.Packages.props`
- [ ] 2.2 Add `Azure.Identity` package reference to `src/Arius.Cli/Arius.Cli.csproj`
- [ ] 2.3 Verify `dotnet build` succeeds with new dependency

## 3. Implement credential resolution and preflight in CliBuilder

- [ ] 3.1 Define `PreflightMode` enum (`ReadOnly`, `ReadWrite`) and `PreflightException` class in `src/Arius.Cli/CliBuilder.cs`
- [ ] 3.2 Change `BuildProductionServices` to `async Task<IServiceProvider>` with signature `(string accountName, string? key, string? passphrase, string containerName, PreflightMode preflightMode)`
- [ ] 3.3 Implement credential resolution: if key is non-null use `StorageSharedKeyCredential`, otherwise construct `BlobServiceClient` with `AzureCliCredential`
- [ ] 3.4 Implement preflight check: `ReadWrite` mode uploads and deletes `.arius-preflight-probe`, `ReadOnly` mode calls `container.ExistsAsync()`
- [ ] 3.5 Implement error translation: catch `CredentialUnavailableException`, `RequestFailedException` (403, 404) and wrap in `PreflightException` with user-friendly messages per Decision 6
- [ ] 3.6 Update the factory delegate type in `BuildRootCommand` to `Func<string, string?, string?, string, PreflightMode, Task<IServiceProvider>>`
- [ ] 3.7 Update `--key` option description to `"Azure Storage account key (omit to use Azure CLI login)"`

## 4. Update verb commands

- [ ] 4.1 Update `ArchiveVerb.Build()` to accept the new async factory delegate, pass `PreflightMode.ReadWrite`, await the factory, and catch `PreflightException`
- [ ] 4.2 Update `RestoreVerb.Build()` to accept the new async factory delegate, pass `PreflightMode.ReadOnly`, await the factory, and catch `PreflightException`
- [ ] 4.3 Update `LsVerb.Build()` to accept the new async factory delegate, pass `PreflightMode.ReadOnly`, await the factory, and catch `PreflightException`
- [ ] 4.4 Remove the key-null error check from each verb (no longer needed — fallback to AzureCliCredential)
- [ ] 4.5 Add `PreflightException` catch block to each verb: display `[red]Error:[/] {message}` via `AnsiConsole.MarkupLine`, log full exception, return exit code 1

## 5. Add global exception handler

- [ ] 5.1 Wrap the root command invocation in `Program.cs` with `try/catch/finally`
- [ ] 5.2 In `catch`: render via `AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything | ExceptionFormats.ShowLinks)` and log `Log.Fatal`
- [ ] 5.3 In `finally`: call `Log.CloseAndFlush()`

## 6. Update tests

- [ ] 6.1 Update test code that provides the service provider factory delegate to match the new async signature with `PreflightMode` parameter
- [ ] 6.2 Verify all existing tests pass with `dotnet test`

## 7. Final verification

- [ ] 7.1 Run full `dotnet build` for the solution
- [ ] 7.2 Run full `dotnet test` for the solution
- [ ] 7.3 Verify `--help` output shows updated `--key` description
