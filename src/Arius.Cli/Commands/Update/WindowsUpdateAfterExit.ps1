param(
    [Parameter(Mandatory = $true)]
    [int]$PidToWait,

    [Parameter(Mandatory = $true)]
    [string]$SourcePath,

    [Parameter(Mandatory = $true)]
    [string]$DestinationPath,

    [Parameter(Mandatory = $true)]
    [string]$TempDir
)

# Windows keeps the running executable locked, so the updater waits for Arius to
# exit before replacing the installed binary.
try {
    Wait-Process -Id $PidToWait -ErrorAction SilentlyContinue
} catch {
}

$copied = $false
for ($i = 0; $i -lt 10 -and -not $copied; $i++) {
    try {
        Copy-Item -LiteralPath $SourcePath -Destination $DestinationPath -Force
        $copied = $true
    } catch {
        Start-Sleep -Milliseconds 200
    }
}

if ($copied) {
    Remove-Item -LiteralPath $SourcePath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $TempDir -Recurse -Force -ErrorAction SilentlyContinue
}
