[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Installer validation requires Windows.' }
$repo = Split-Path $PSScriptRoot -Parent
$installer = @(Get-ChildItem "$repo/artifacts/packages/win-x64/*-setup.exe")
if ($installer.Count -ne 1) { throw 'Build exactly one Windows installer first.' }
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ("VibeRe installer test " + [Guid]::NewGuid().ToString('N'))
$install = Join-Path $sandbox 'installed apps'
New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
$uninstaller = Join-Path $install 'unins000.exe'
function Invoke-Setup {
    param([string]$File, [string[]]$Options)
    $process = Start-Process -FilePath $File -ArgumentList $Options -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode) { throw "Setup failed: $($process.ExitCode). Logs: $sandbox" }
}
try {
    # Do not replace a real user's registered installation. CI uses a clean account.
    $key = 'HKCU:/Software/Microsoft/Windows/CurrentVersion/Uninstall/{4C0B4424-0E54-4A49-912E-C6311CED7731}_is1'
    if (Test-Path -LiteralPath $key) { throw 'An existing VibeRe installation is registered. Use a clean test account.' }
    $options = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', "/DIR=`"$install`"", "/LOG=`"$sandbox/install.log`"")
    Invoke-Setup $installer[0].FullName $options
    if (!(Test-Path -LiteralPath $uninstaller)) { throw 'Uninstaller missing.' }
    if (!(Test-Path -LiteralPath $key)) { throw 'Per-user uninstall registration missing.' }
    $menu = Join-Path ([Environment]::GetFolderPath('Programs')) 'VibeRe'
    $shell = New-Object -ComObject WScript.Shell
    $desktopShortcut = $shell.CreateShortcut((Join-Path $menu 'VibeRe.lnk'))
    $cliShortcut = $shell.CreateShortcut((Join-Path $menu 'VibeRe CLI.lnk'))
    if ($desktopShortcut.TargetPath -ne (Join-Path $install 'desktop/VibeRe.Desktop.exe')) { throw 'Desktop shortcut target is incorrect.' }
    if ($cliShortcut.WorkingDirectory -ne (Join-Path $install 'cli') -or !$cliShortcut.Arguments.Contains('vibe-re.exe')) { throw 'CLI shortcut target is incorrect.' }
    # Test the installed tree before and after an in-place reinstall/upgrade.
    foreach ($pass in 1..2) {
        dotnet run --project "$repo/build/SmokeTests" -c Release -- --installed-root $install
        if ($LASTEXITCODE) { throw "Installed startup smoke test failed (pass $pass)." }
        if ($pass -eq 1) { Invoke-Setup $installer[0].FullName $options }
    }
    # Uninstall must not delete arbitrary user files, even within the install tree.
    Set-Content (Join-Path $install 'user-data.txt') 'preserve me'
    Invoke-Setup $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$sandbox/uninstall.log`"")
    if (Test-Path -LiteralPath (Join-Path $install 'cli/vibe-re.exe')) { throw 'Installed CLI remained after uninstall.' }
    if (Test-Path -LiteralPath $key) { throw 'Uninstall registration remained.' }
    if (!(Test-Path -LiteralPath (Join-Path $install 'user-data.txt'))) { throw 'Uninstall removed user data.' }
    Write-Host 'PASS: per-user install, installed startup, reinstall, uninstall and user-file preservation'
} finally {
    # On failure retain logs and let the operator inspect the installation.
    $logs = Join-Path $repo 'artifacts/installer-logs'
    New-Item -ItemType Directory -Path $logs -Force | Out-Null
    Get-ChildItem -LiteralPath $sandbox -Filter '*.log' -File | Copy-Item -Destination $logs -Force
    Write-Host "Installer test logs: $sandbox"
}
