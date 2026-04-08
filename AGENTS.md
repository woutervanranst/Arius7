<!-- https://dev.to/webdeveloperhyper/how-to-make-ai-follow-your-instructions-more-for-free-openspec-2c85 -->

# AGENTS.md

- When making code changes, always run the tests.
- When the tests pass, make a conventional git commit.
- Work Test-Driven: first, write a failing test. Then, implement

## Session Rules

- Always update `README.md` to reflect the current state of the project

## Testing

This project uses **TUnit** (not xUnit/NUnit). Key differences:

- **Run tests**: `dotnet test --project <path-to-csproj>`
- **Filter by class**: use `--treenode-filter "/*/*/<ClassName>/*"` (NOT `--filter`)
- **Filter by test name**: use `--treenode-filter "/*/*/*/<TestMethodName>"`
- **List tests**: `dotnet test --project <path-to-csproj> --list-tests`
- The standard `--filter` flag does NOT work with TUnit; it silently runs zero tests.