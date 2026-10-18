#requires -Version 7.4
# Tests native update application using a disposable, uniquely named fixture.
# Never installs/updates VibeRe, connects to a public feed or accesses user data.
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('win-x64', 'linux-x64', 'osx-arm64')][string]$Runtime)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if ([Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier -ne $Runtime) { throw 'Use a native runner for Velopack smoke tests.' }
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ("VibeRe Velopack test " + [Guid]::NewGuid().ToString('N'))
$fixtureId = 'GijsRommers.VibeRe.Fixture.' + [Guid]::NewGuid().ToString('N')
$feed = "$sandbox/feed"
$install = "$sandbox/installed apps"
$report = "$sandbox/result.json"
New-Item -ItemType Directory -Path $feed, $install -Force | Out-Null
$extension = if ($Runtime -eq 'win-x64') { '.exe' } else { '' }
$icon = switch ($Runtime) {
    'win-x64' { "$repo/VibeRe.Desktop/Assets/avalonia-logo.ico" }
    'linux-x64' { "$repo/packaging/assets/app.png" }
    'osx-arm64' { "$repo/packaging/assets/app.icns" }
}
$oldExtract = $env:APPIMAGE_EXTRACT_AND_RUN
$installedWindows = $false
Start-Transcript -Path "$sandbox/native-smoke.log" | Out-Null
function Invoke-Process([string]$File, [string[]]$Options) {
    $info = [Diagnostics.ProcessStartInfo]::new($File)
    $info.UseShellExecute = $false
    foreach ($argument in $Options) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info)
    if (!$process.WaitForExit(120000)) { $process.Kill($true); throw "Process timed out: $File" }
    if ($process.ExitCode) { throw "Process failed ($($process.ExitCode)): $File" }
    $process.Dispose()
}
function Assert-Report([string]$Version) {
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    while (!(Test-Path -LiteralPath $report)) {
        if ([DateTime]::UtcNow -gt $deadline) { throw "Native update did not restart version $Version. Logs: $sandbox" }
        Start-Sleep -Milliseconds 250
    }
    $value = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    if ($value.Version -ne $Version -or $value.AppId -ne $fixtureId) { throw "Wrong native fixture identity/version: $($value | ConvertTo-Json -Compress)" }
}
Push-Location $repo
try {
    dotnet tool restore --tool-manifest "$repo/.config/dotnet-tools.json"
    if ($LASTEXITCODE) { throw 'Velopack tool restore failed.' }
    dotnet publish "$repo/build/VelopackSmokeTests" -c Release -r $Runtime --self-contained true -o "$sandbox/publish" -p:Version=1.0.0 -p:PublishSingleFile=false -p:PublishTrimmed=false
    if ($LASTEXITCODE) { throw 'Fixture publish failed.' }
    foreach ($version in @('1.0.0', '1.0.1')) {
        dotnet tool run vpk --skip-updates pack --packId $fixtureId --packTitle $fixtureId --packVersion $version --runtime $Runtime --channel "$Runtime-fixture" --packDir "$sandbox/publish" --mainExe "VibeRe.VelopackFixture$extension" --icon $icon --outputDir $feed --delta None
        if ($LASTEXITCODE) { throw "Fixture packaging $version failed." }
        if ($version -eq '1.0.0') {
            switch ($Runtime) {
                'win-x64' {
                    $setup = @(Get-ChildItem -LiteralPath $feed -Filter '*Setup.exe' -File)
                    if ($setup.Count -ne 1) { throw 'Fixture setup missing.' }
                    Invoke-Process $setup[0].FullName @('--silent', '--installto', $install, '--log', "$sandbox/install.log")
                    $installedWindows = $true
                    $executable = "$install/current/VibeRe.VelopackFixture.exe"
                }
                'linux-x64' {
                    $appImage = @(Get-ChildItem -LiteralPath $feed -Filter '*.AppImage' -File)
                    if ($appImage.Count -ne 1) { throw 'Fixture AppImage missing.' }
                    $executable = "$install/Fixture.AppImage"
                    Copy-Item -LiteralPath $appImage[0].FullName -Destination $executable
                    chmod +x $executable
                    if ($LASTEXITCODE) { throw 'Could not mark fixture AppImage executable.' }
                    # Exercise the real AppImage and updater without needing a FUSE mount.
                    $env:APPIMAGE_EXTRACT_AND_RUN = '1'
                }
                'osx-arm64' {
                    $portable = @(Get-ChildItem -LiteralPath $feed -Filter '*Portable.zip' -File)
                    if ($portable.Count -ne 1) { throw 'Fixture app bundle missing.' }
                    ditto -x -k $portable[0].FullName $install
                    if ($LASTEXITCODE) { throw 'Fixture app bundle extraction failed.' }
                    $app = @(Get-ChildItem -LiteralPath $install -Directory -Filter '*.app')
                    if ($app.Count -ne 1) { throw 'Fixture app bundle missing.' }
                    $executable = "$($app[0].FullName)/Contents/MacOS/VibeRe.VelopackFixture"
                }
            }
            Invoke-Process $executable @('inspect', $feed, $report)
            Assert-Report '1.0.0'
            Remove-Item -LiteralPath $report
        }
    }
    Invoke-Process $executable @('update', $feed, $report)
    Assert-Report '1.0.1'
    # Verify the installed artifact itself changed, rather than just a one-off
    # restart of a newer file somewhere else.
    Remove-Item -LiteralPath $report
    Invoke-Process $executable @('inspect', $feed, $report)
    Assert-Report '1.0.1'
    Write-Host "PASS: $Runtime native fixture install, local-feed download, real apply and restart (1.0.0 -> 1.0.1)"
} finally {
    $env:APPIMAGE_EXTRACT_AND_RUN = $oldExtract
    if ($installedWindows -and (Test-Path -LiteralPath "$install/Update.exe")) {
        Invoke-Process "$install/Update.exe" @('--silent', 'uninstall')
    }
    Stop-Transcript | Out-Null
    $logs = "$repo/artifacts/velopack-logs/$Runtime"
    New-Item -ItemType Directory -Path $logs -Force | Out-Null
    Get-ChildItem -LiteralPath $sandbox -Filter '*.log' -File | Copy-Item -Destination $logs -Force
    $nativeLog = Join-Path ([IO.Path]::GetTempPath()) "velopack_$fixtureId.log"
    if (Test-Path -LiteralPath $nativeLog) { Copy-Item -LiteralPath $nativeLog -Destination "$logs/updater.log" -Force }
    if (Test-Path -LiteralPath $report) { Copy-Item -LiteralPath $report -Destination "$logs/result.json" -Force }
    Pop-Location
    Write-Host "Fixture retained for diagnostics: $sandbox"
}
