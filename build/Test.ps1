[CmdletBinding()]
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    dotnet build vibe-re.Desktop.slnf -c $Configuration --artifacts-path "$repo/artifacts/build"
    if ($LASTEXITCODE) { throw 'Build failed.' }
    dotnet test vibe-re.Desktop.slnf -c $Configuration --no-build --artifacts-path "$repo/artifacts/build" --logger trx --results-directory "$repo/artifacts/test-results"
    if ($LASTEXITCODE) { throw 'Tests failed.' }
} finally { Pop-Location }
