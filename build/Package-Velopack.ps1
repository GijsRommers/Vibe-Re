#requires -Version 7.4
# Native Velopack packages. Run Package.ps1 -Velopack for publishing + both layouts.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('win-x64', 'linux-x64', 'osx-arm64')][string]$Runtime,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')][string]$Version
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$nativeRid = [Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier
if ($nativeRid -ne $Runtime) { throw "Build Velopack natively: requested $Runtime, current runtime $nativeRid." }
$stage = "$repo/artifacts/stage/$Runtime"
$app = "$repo/artifacts/velopack-stage/$Runtime"
$dist = "$repo/artifacts/packages/$Runtime/velopack"
# Never overlay the Inno/portable tree or a previously generated update feed.
foreach ($directory in @($app, $dist)) {
    if (Test-Path -LiteralPath $directory) { Remove-Item -LiteralPath $directory -Recurse -Force }
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}
foreach ($source in @('desktop', 'cli')) {
    if (!(Test-Path -LiteralPath "$stage/$source" -PathType Container)) { throw "Publish the portable $source tree first with Package.ps1." }
}
# Desktop's own server publish is already complete. Preserve it as-is; the CLI
# gets its own complete runtime and server subtree to avoid assembly collisions.
Copy-Item "$stage/desktop/*" $app -Recurse -Force
Copy-Item "$stage/cli" "$app/cli" -Recurse -Force
Copy-Item "$stage/INSTALL.md", "$stage/build-info.json" $app -Force
$extension = if ($Runtime -eq 'win-x64') { '.exe' } else { '' }
$icon = switch ($Runtime) {
    'win-x64' { "$repo/VibeRe.Desktop/Assets/avalonia-logo.ico" }
    'linux-x64' { "$repo/packaging/assets/app.png" }
    'osx-arm64' { "$repo/packaging/assets/app.icns" }
}
foreach ($file in @("VibeRe.Desktop$extension", 'VibeRe.Desktop.runtimeconfig.json', 'Velopack.dll',
        "cli/vibe-re$extension", 'cli/vibe-re.runtimeconfig.json',
        "server/vibe-re-server$extension", 'server/vibe-re-server.runtimeconfig.json',
        'server/wwwroot/index.html', 'server/wwwroot/_framework/blazor.webassembly.js',
        "cli/server/vibe-re-server$extension", 'cli/server/wwwroot/index.html')) {
    if (!(Test-Path -LiteralPath "$app/$file" -PathType Leaf)) { throw "Incomplete Velopack application: $file" }
}
Push-Location $repo
try {
    dotnet tool restore --tool-manifest "$repo/.config/dotnet-tools.json"
    if ($LASTEXITCODE) { throw 'Pinned Velopack tool restore failed.' }
    $options = @('--skip-updates', 'pack', '--packId', 'GijsRommers.VibeRe', '--packTitle', 'VibeRe',
        '--packVersion', $Version, '--runtime', $Runtime, '--channel', "$Runtime-stable", '--packDir', $app,
        '--mainExe', "VibeRe.Desktop$extension", '--icon', $icon, '--outputDir', $dist, '--delta', 'None')
    if ($Runtime -eq 'linux-x64') { $options += @('--categories', 'Development;Utility') }
    dotnet tool run vpk @options
    if ($LASTEXITCODE) { throw 'Velopack packaging failed.' }
    & "$PSScriptRoot/Test-VelopackPackage.ps1" -Runtime $Runtime -Version $Version
    Get-ChildItem -LiteralPath $dist -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object Name | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
    } | Set-Content "$dist/SHA256SUMS.txt" -Encoding ASCII
    Write-Host "Velopack packages and local feed: $dist"
} finally { Pop-Location }
