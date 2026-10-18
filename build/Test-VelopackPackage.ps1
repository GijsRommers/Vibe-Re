#requires -Version 7.4
# Inspect the actual native deliverable, not only the input publish directory.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('win-x64', 'linux-x64', 'osx-arm64')][string]$Runtime,
    [Parameter(Mandatory)][string]$Version
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$dist = "$repo/artifacts/packages/$Runtime/velopack"
$app = "$repo/artifacts/velopack-stage/$Runtime"
$feed = Get-Content -LiteralPath "$dist/releases.$Runtime-stable.json" -Raw | ConvertFrom-Json
$full = @($feed.Assets | Where-Object { $_.Type -eq 'Full' -and $_.Version -eq $Version })
if ($full.Count -ne 1) { throw "Expected exactly one full release for $Version." }
$asset = $full[0]
if ([IO.Path]::GetFileName($asset.FileName) -ne $asset.FileName) { throw 'Feed contains an unsafe filename.' }
$package = Join-Path $dist $asset.FileName
if (!(Test-Path -LiteralPath $package -PathType Leaf)) { throw 'Full update package missing.' }
if ((Get-Item -LiteralPath $package).Length -ne $asset.Size) { throw 'Release size does not match feed.' }
if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne $asset.SHA256) { throw 'Release SHA-256 does not match feed.' }
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ("VibeRe package inspection " + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($package)
    try {
        $specs = @($zip.Entries | Where-Object { $_.FullName -match '^[^/]+\.nuspec$' })
        if ($specs.Count -ne 1) { throw 'Expected a single package manifest.' }
        $reader = [IO.StreamReader]::new($specs[0].Open())
        try { [xml]$spec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($spec.package.metadata.id -ne 'GijsRommers.VibeRe' -or
            $spec.package.metadata.version -ne $Version -or
            $spec.package.metadata.channel -ne "$Runtime-stable" -or
            $spec.package.metadata.rid -ne $Runtime) { throw 'Incorrect package identity, version, channel or runtime.' }
        if ($Runtime -eq 'linux-x64') {
            $imageEntry = $zip.GetEntry('lib/app/GijsRommers.VibeRe.AppImage')
            if (!$imageEntry) { throw 'Linux update package does not contain its AppImage.' }
            $imagePath = "$dist/GijsRommers.VibeRe-linux-x64-stable.AppImage"
            $stream = $imageEntry.Open()
            try { $imageHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
            if ($imageHash -ne (Get-FileHash -LiteralPath $imagePath -Algorithm SHA256).Hash) {
                throw 'Linux installer AppImage differs from the update package.'
            }
        }
    } finally { $zip.Dispose() }
    switch ($Runtime) {
        'win-x64' {
            $setups = @(Get-ChildItem -LiteralPath $dist -Filter '*Setup.exe' -File)
            if ($setups.Count -ne 1) { throw 'Expected one Velopack Windows Setup.exe.' }
            [IO.Compression.ZipFile]::ExtractToDirectory($package, "$sandbox/package")
            $payload = "$sandbox/package/lib/app"
        }
        'linux-x64' {
            $images = @(Get-ChildItem -LiteralPath $dist -Filter '*.AppImage' -File)
            if ($images.Count -ne 1) { throw 'Expected one Linux AppImage.' }
            Push-Location $sandbox
            try {
                & $images[0].FullName --appimage-extract | Out-Null
                if ($LASTEXITCODE) { throw 'AppImage extraction failed.' }
            } finally { Pop-Location }
            $payload = "$sandbox/squashfs-root/usr/bin"
        }
        'osx-arm64' {
            $installers = @(Get-ChildItem -LiteralPath $dist -Filter '*.pkg' -File)
            if ($installers.Count -ne 1) { throw 'Expected one macOS PKG installer.' }
            # Inspect the PKG payload without installing into /Applications.
            pkgutil --expand-full $installers[0].FullName "$sandbox/installer"
            if ($LASTEXITCODE) { throw 'macOS PKG expansion failed.' }
            $installedApps = @(Get-ChildItem -LiteralPath "$sandbox/installer" -Recurse -Directory -Filter '*.app')
            if ($installedApps.Count -ne 1) { throw 'macOS PKG must contain one .app.' }
            $installerPayload = "$($installedApps[0].FullName)/Contents/MacOS"
            $bundles = @(Get-ChildItem -LiteralPath $dist -Filter '*Portable.zip' -File)
            if ($bundles.Count -ne 1) { throw 'Expected one portable macOS app bundle archive.' }
            ditto -x -k $bundles[0].FullName "$sandbox/bundle"
            if ($LASTEXITCODE) { throw 'macOS bundle extraction failed.' }
            $apps = @(Get-ChildItem -LiteralPath "$sandbox/bundle" -Directory -Filter '*.app')
            if ($apps.Count -ne 1) { throw 'macOS portable archive must contain one .app.' }
            $payload = "$($apps[0].FullName)/Contents/MacOS"
            if (!(Test-Path -LiteralPath "$($apps[0].FullName)/Contents/Info.plist")) { throw 'macOS Info.plist missing.' }
        }
    }
    $extension = if ($Runtime -eq 'win-x64') { '.exe' } else { '' }
    $required = @("VibeRe.Desktop$extension", 'VibeRe.Desktop.dll', 'VibeRe.Desktop.runtimeconfig.json', 'Velopack.dll',
        "cli/vibe-re$extension", 'cli/vibe-re.dll', 'cli/vibe-re.runtimeconfig.json')
    foreach ($server in @('server', 'cli/server')) {
        $required += @("$server/vibe-re-server$extension", "$server/vibe-re-server.runtimeconfig.json",
            "$server/wwwroot/index.html", "$server/wwwroot/_framework/blazor.webassembly.js")
        $wasm = @(Get-ChildItem -LiteralPath "$payload/$server/wwwroot/_framework" -Filter '*.wasm' -File)
        if ($wasm.Count -eq 0) { throw "Compiled WebAssembly payload missing from $server." }
    }
    # Validate every shipped input file, including native libraries/configuration,
    # against the extracted delivery artifact. These exclusions match pinned vpk
    # 1.2.161 defaults, rather than silently accepting arbitrary missing files.
    $required += @(Get-ChildItem -LiteralPath $app -File -Recurse | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($app, $_.FullName).Replace('\', '/')
        if ($relative -notmatch '(^|/)createdump[^/]*|\.vshost\.|\.nupkg$|\.pdb$') { $relative }
    })
    $updateZip = if ($Runtime -eq 'osx-arm64') { [IO.Compression.ZipFile]::OpenRead($package) } else { $null }
    try {
        foreach ($relative in ($required | Select-Object -Unique)) {
            if (!(Test-Path -LiteralPath "$payload/$relative" -PathType Leaf)) { throw "Native package is incomplete: $relative" }
            if ((Get-FileHash -LiteralPath "$payload/$relative").Hash -ne (Get-FileHash -LiteralPath "$app/$relative").Hash) {
                throw "Native package differs from staged publish: $relative"
            }
            if ($updateZip) {
                if (!(Test-Path -LiteralPath "$installerPayload/$relative" -PathType Leaf) -or
                    (Get-FileHash -LiteralPath "$installerPayload/$relative" -Algorithm SHA256).Hash -ne
                    (Get-FileHash -LiteralPath "$app/$relative" -Algorithm SHA256).Hash) {
                    throw "macOS PKG differs from staged publish: $relative"
                }
                $entry = $updateZip.GetEntry("lib/app/Contents/MacOS/$relative")
                if (!$entry) { throw "macOS update package is missing $relative" }
                $stream = $entry.Open()
                try { $entryHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
                if ($entryHash -ne (Get-FileHash -LiteralPath "$app/$relative" -Algorithm SHA256).Hash) {
                    throw "macOS update package differs from staged publish: $relative"
                }
            }
        }
    } finally { if ($updateZip) { $updateZip.Dispose() } }
    Write-Host "PASS: $Runtime native package contents, identity, feed size and SHA-256"
} finally {
    Remove-Item -LiteralPath $sandbox -Recurse -Force
}
