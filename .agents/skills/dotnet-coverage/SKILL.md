---
name: dotnet-coverage
description: Run .NET tests with dotnet-coverage, especially on macOS arm64 or TUnit/Microsoft.Testing.Platform projects where dynamic instrumentation may produce empty reports. Use when users mention dotnet-coverage, Codecov patch coverage, Cobertura reports, or missing coverage on changed .NET lines.
---

# dotnet-coverage

## When To Use

Use this skill when:
- A user asks to run or diagnose `dotnet-coverage`.
- Codecov reports missing patch coverage for .NET code.
- A local coverage run says `No code coverage data available. Profiler was not initialized.`
- Coverage reports are empty, for example Cobertura with `<packages />`.
- The repo uses TUnit or Microsoft.Testing.Platform and `dotnet-coverage collect` behaves differently from CI.

## macOS arm64 Caveat

`dotnet-coverage` dynamic instrumentation does not produce usable coverage on macOS arm64. On Apple Silicon, a command can run all tests successfully and still print:

```text
No code coverage data available. Profiler was not initialized.
```

Do not treat that as proof of zero coverage or missing tests. It means the local collector did not attach.

Check the CI workflow before choosing a local command. This repo intentionally skips coverage on GitHub-hosted macOS because those runners are arm64; coverage is collected on Linux and Windows instead.

## Preferred Workflow

1. Identify the changed production files and relevant test project.
2. Run the relevant tests normally first, using TUnit's `--treenode-filter` when targeting classes or methods.
3. If local dynamic coverage works on the current OS, use the CI-style command.
4. If running on macOS arm64, or if dynamic coverage produces no packages, use Linux x64 plus static instrumentation.
5. Inspect file-level and changed-line coverage from the generated Cobertura XML instead of relying only on the aggregate percentage.

## CI-Style Dynamic Command

Use this where dynamic instrumentation is supported, such as Linux x64 or Windows:

```bash
dotnet-coverage collect --output Arius.Core.Tests.coverage.cobertura.xml --output-format cobertura "dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --no-build -c Release"
```

If this command passes tests but emits an empty report, switch to static instrumentation.

## Static Instrumentation Fallback

Static instrumentation works when dynamic profiler attachment does not. Build first, then pass the production assembly to `--include-files`:

```bash
dotnet restore src/Arius.Core.Tests/Arius.Core.Tests.csproj
dotnet build src/Arius.Core.Tests/Arius.Core.Tests.csproj --no-restore -c Release
dotnet-coverage collect \
  --include-files src/Arius.Core.Tests/bin/Release/net10.0/Arius.Core.dll \
  --output .coverage/Arius.Core.Tests.static.coverage.cobertura.xml \
  --output-format cobertura \
  "dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --no-build -c Release"
```

Use the test project's output directory for `--include-files` because that is where the test host loads the production assembly.

## Apple Silicon Docker Fallback

On Apple Silicon, Linux arm64 containers can hit the same profiler issue. Prefer a Linux x64 SDK container for CI-like coverage:

```bash
docker run --rm --platform linux/amd64 \
  -v "$PWD":"/workspace" \
  -w "/workspace" \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  sh -lc 'set -e; dotnet tool install --global dotnet-coverage >/dev/null; export PATH="$PATH:/root/.dotnet/tools"; dotnet restore src/Arius.Core.Tests/Arius.Core.Tests.csproj >/dev/null; dotnet build src/Arius.Core.Tests/Arius.Core.Tests.csproj --no-restore -c Release >/dev/null; dotnet-coverage collect --include-files /workspace/src/Arius.Core.Tests/bin/Release/net10.0/Arius.Core.dll --output /workspace/.coverage/Arius.Core.Tests.static.coverage.cobertura.xml --output-format cobertura "dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --no-build -c Release"'
```

If test binaries were previously built for a different runtime architecture, clean or rebuild inside the same x64 container before running `--no-build`. Otherwise Microsoft.Testing.Platform can try to launch a stale architecture-specific test executable and report zero tests.

## Interpreting Results

A real Cobertura report has package and class entries:

```xml
<coverage line-rate="...">
  <packages>
    <package name="Arius.Core">
```

An unusable report often looks like this:

```xml
<coverage line-rate="1" branch-rate="1" complexity="1">
  <packages />
</coverage>
```

Do not upload or trust an empty `<packages />` report.

## Quick Changed-File Summary

Use a small XML parser to summarize specific changed files from Cobertura. Example:

```bash
ruby -r rexml/document -e 'doc=REXML::Document.new(File.read(".coverage/Arius.Core.Tests.static.coverage.cobertura.xml")); targets=%w[src/Arius.Core/Features/ListQuery/ListQueryHandler.cs src/Arius.Core/Features/ListQuery/LocalDirectoryReader.cs]; targets.each do |t| classes=REXML::XPath.match(doc, "//class[contains(@filename, #{t.dump})]"); lines={}; classes.each do |c| REXML::XPath.each(c, "./lines/line") { |l| n=l.attributes["number"].to_i; h=l.attributes["hits"].to_i; lines[n]=[lines.fetch(n, 0), h].max } end; covered=lines.values.count { |h| h>0 }; puts "#{t}: #{covered}/#{lines.size}" end'
```

For Codecov complaints, prioritize important changed behavior over mechanically covering every trivial line. Add focused behavior tests for uncovered paths that matter.
