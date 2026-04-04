# Suggested Commands

## Build
```bash
dotnet build src/Arius.slnx
```

## Test (TUnit - NOT xUnit)
```bash
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj
dotnet test --project src/Arius.AzureBlob.Tests/Arius.AzureBlob.Tests.csproj
```

### Filter by class (TUnit-specific)
```bash
dotnet test --project <path> --treenode-filter "/*/*/<ClassName>/*"
```

### Filter by test name
```bash
dotnet test --project <path> --treenode-filter "/*/*/*/<TestMethodName>"
```

**WARNING**: `--filter` does NOT work with TUnit. Use `--treenode-filter` instead.

## Git
Standard git commands. Conventional commits required.
