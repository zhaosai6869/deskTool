param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

# Build the GitHub Release zip consumed by the built-in online updater.
# Usage: .\pack-release.ps1 -Version 1.0.1
# Then create a Release with tag v1.0.1 and upload the generated zip.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root 'ZhuangJiSoft\ZhuangJiSoft.csproj'
$stage = Join-Path $root 'release-stage'
$zip = Join-Path $root ("DeskApp-v" + $Version + ".zip")

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must look like 1.0.1"
}

# Keep the assembly version in sync; otherwise the updater keeps offering the same release.
$content = Get-Content $proj -Raw
if (-not [regex]::IsMatch($content, '<Version>[^<]*</Version>')) {
    throw 'Could not find <Version> in csproj'
}
$updated = [regex]::Replace($content, '<Version>[^<]*</Version>', "<Version>$Version</Version>")
if ($updated -ne $content) {
    Set-Content -Path $proj -Value $updated -Encoding UTF8 -NoNewline
}

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }

& dotnet publish $proj -c Release -o $stage
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# Never ship local runtime data, login state or update staging folders.
foreach ($name in @('data', 'updates', '*_cache')) {
    Get-ChildItem -Path $stage -Directory -Filter $name -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force
}

# Windows-only app: drop SQLite native libs for Linux/macOS/wasm/browser RIDs (about 19 MB).
$runtimes = Join-Path $stage 'runtimes'
if (Test-Path $runtimes) {
    Get-ChildItem -Path $runtimes -Directory |
        Where-Object { $_.Name -notlike 'win-*' } |
        Remove-Item -Recurse -Force
}

Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
Remove-Item $stage -Recurse -Force

Write-Host "Release zip: $zip"
Write-Host "Next: create GitHub Release tag v$Version and upload this zip."
