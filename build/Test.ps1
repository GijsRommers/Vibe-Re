[CmdletBinding()]
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    dotnet build vibe-re.sln -c $Configuration --artifacts-path "$repo/artifacts/build"
    if ($LASTEXITCODE) { throw 'Build failed.' }
    # Avoid competing test hosts starving the short transport/readiness deadline fixtures on CI.
    dotnet test vibe-re.sln -c $Configuration --no-build -m:1 --artifacts-path "$repo/artifacts/build" --logger trx --results-directory "$repo/artifacts/test-results"
    if ($LASTEXITCODE) { throw 'Tests failed.' }
} finally { Pop-Location }
