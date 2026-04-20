param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('linux', 'macos', 'windows')]
    [string]$RunnerOs,

    [Parameter(Mandatory = $true)]
    [ValidateSet('test', 'build')]
    [string]$Mode
)

$ErrorActionPreference = 'Stop'

function Get-ProjectTfms {
    param([xml]$ProjectXml)

    $targetFrameworks = @()

    foreach ($propertyGroup in $ProjectXml.Project.PropertyGroup) {
        if ($propertyGroup.TargetFramework) {
            $targetFrameworks += [string]$propertyGroup.TargetFramework
        }

        if ($propertyGroup.TargetFrameworks) {
            $targetFrameworks += ([string]$propertyGroup.TargetFrameworks -split ';')
        }
    }

    return $targetFrameworks |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ } |
        Select-Object -Unique
}

function Test-IsWindowsOnlyProject {
    param([string[]]$TargetFrameworks)

    if ($TargetFrameworks.Count -eq 0) {
        return $false
    }

    return ($TargetFrameworks | Where-Object { $_ -notmatch '-windows' }).Count -eq 0
}

function Test-RequiresLinuxRunner {
    param([xml]$ProjectXml)

    $hasDirectTestcontainersReference = ($ProjectXml.Project.ItemGroup.PackageReference | Where-Object {
        [string]$_.Include -match '^Testcontainers(?:\.|$)'
    }).Count -gt 0

    if ($hasDirectTestcontainersReference) {
        return $true
    }

    return ($ProjectXml.Project.ItemGroup.ProjectReference | Where-Object {
        [string]$_.Include -match 'Arius\.Integration\.Tests\.csproj$'
    }).Count -gt 0
}

$workspaceRoot = Resolve-Path (Join-Path $PSScriptRoot '..' '..')
$srcRoot = Join-Path $workspaceRoot 'src'
$isWindowsRunner = $RunnerOs -eq 'windows'

$projects = Get-ChildItem -Path $srcRoot -Recurse -Filter '*.csproj' |
    Sort-Object FullName |
    ForEach-Object {
        $projectPath = $_.FullName
        [xml]$projectXml = Get-Content -Path $projectPath -Raw
        $targetFrameworks = Get-ProjectTfms -ProjectXml $projectXml
        $isWindowsOnly = Test-IsWindowsOnlyProject -TargetFrameworks $targetFrameworks
        $requiresLinuxRunner = Test-RequiresLinuxRunner -ProjectXml $projectXml
        $isTestProject = ($projectXml.Project.PropertyGroup | Where-Object {
            [string]$_.TestingPlatformDotnetTestSupport -eq 'true'
        }).Count -gt 0

        [pscustomobject]@{
            RelativePath = [System.IO.Path]::GetRelativePath($workspaceRoot, $projectPath).Replace('\', '/')
            TargetFrameworks = $targetFrameworks
            IsWindowsOnly = $isWindowsOnly
            RequiresLinuxRunner = $requiresLinuxRunner
            IsTestProject = $isTestProject
        }
    } |
    Where-Object {
        if ($Mode -eq 'test' -and -not $_.IsTestProject) {
            return $false
        }

        if ($RunnerOs -ne 'linux' -and $_.RequiresLinuxRunner) {
            return $false
        }

        return $isWindowsRunner -or -not $_.IsWindowsOnly
    }

$filteredProjects = $projects.RelativePath

if (-not $filteredProjects) {
    throw "No projects matched mode '$Mode' for runner '$RunnerOs'."
}

$json = ConvertTo-Json -InputObject @($filteredProjects) -Compress
"projects=$json" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
