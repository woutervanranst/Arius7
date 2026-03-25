# AGENTS.md

When making code changes, always run the tests.
When the tests pass, make a conventional git commit.

## Testing

This project uses **TUnit** (not xUnit/NUnit). Key differences:

- **Run tests**: `dotnet test --project <path-to-csproj>`
- **Filter by class**: use `--treenode-filter "/*/*/<ClassName>/*"` (NOT `--filter`)
- **Filter by test name**: use `--treenode-filter "/*/*/*/<TestMethodName>"`
- **List tests**: `dotnet test --project <path-to-csproj> --list-tests`
- The standard `--filter` flag does NOT work with TUnit; it silently runs zero tests.