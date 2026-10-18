[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('win-x64', 'linux-x64', 'osx-arm64', 'osx-x64')][string]$Runtime,
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')][string]$Version = '0.1.0-dev',
    [switch]$WindowsInstaller,
    [string]$IsccPath
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$stage = "$repo/artifacts/stage/$Runtime"
$dist = "$repo/artifacts/packages/$Runtime"
if ($WindowsInstaller -and ($Runtime -ne 'win-x64' -or $env:OS -ne 'Windows_NT')) {
    throw 'The installer must be built on Windows for win-x64.'
}
# Only these fixed, repository-owned staging directories are ever cleared.
foreach ($directory in @($stage, $dist)) {
    $resolved = [IO.Path]::GetFullPath($directory)
    if (!$resolved.StartsWith([IO.Path]::GetFullPath("$repo/artifacts/"), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe output path.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    New-Item -ItemType Directory -Path $resolved -Force | Out-Null
}
Push-Location $repo
try {
    $projects = [ordered]@{ cli = 'VibeRe.Cli'; desktop = 'VibeRe.Desktop'; server = 'VibeRe.Server' }
    foreach ($entry in $projects.GetEnumerator()) {
        # ArtifactsPath isolates bin/obj by project and RID. Never share OutputPath.
        dotnet publish "$($entry.Value)/$($entry.Value).csproj" -c Release -r $Runtime --self-contained true --artifacts-path "$repo/artifacts/publish-build/$Runtime" -o "$stage/$($entry.Key)" "-p:Version=$Version" -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false
        if ($LASTEXITCODE) { throw "Publish failed: $($entry.Value)" }
        $server = if ($entry.Key -eq 'server') { "$stage/server" } else { "$stage/$($entry.Key)/server" }
        foreach ($required in @('vibe-re-server.runtimeconfig.json', 'wwwroot/index.html', 'wwwroot/_framework/blazor.webassembly.js')) {
            if (!(Test-Path -LiteralPath "$server/$required" -PathType Leaf)) { throw "Incomplete $($entry.Key) package: missing $required" }
        }
        if (!(Get-ChildItem -LiteralPath "$server/wwwroot/_framework" -Filter '*.wasm' -File)) {
            throw "Incomplete $($entry.Key) package: compiled WebAssembly assets missing."
        }
    }
    # Native NuGet packages can include large PDBs even with DebugSymbols=false.
    Get-ChildItem -LiteralPath $stage -Recurse -File -Filter '*.pdb' | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
    Copy-Item "$repo/docs/installation.md" "$stage/INSTALL.md"
    @{ version = $Version; runtime = $Runtime; commit = (git rev-parse HEAD); dirty = [bool](git status --porcelain) } | ConvertTo-Json | Set-Content "$stage/build-info.json" -Encoding UTF8
    $name = "vibe-re-$Version-$Runtime"
    if ($Runtime.StartsWith('win-')) {
        Compress-Archive -Path "$stage/*" -DestinationPath "$dist/$name.zip" -CompressionLevel Optimal
    } else {
        # tar preserves Unix executable permissions, unlike upload-artifact's ZIP.
        tar -czf "$dist/$name.tar.gz" -C $stage .
        if ($LASTEXITCODE) { throw 'Archive creation failed.' }
    }
    if ($WindowsInstaller) {
        if (!$IsccPath) { $IsccPath = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }
        if (!$IsccPath) { $IsccPath = "${env:ProgramFiles(x86)}/Inno Setup 6/ISCC.exe" }
        if (!(Test-Path -LiteralPath $IsccPath)) { throw 'Inno Setup 6.3+ is required. Pass -IsccPath or run build/Get-InnoSetup.ps1.' }
        $numericVersion = $Version.Split('-')[0]
        & $IsccPath "/DAppVersion=$Version" "/DAppNumericVersion=$numericVersion" "/DSourceDir=$stage" "/DOutputDir=$dist" "$repo/packaging/windows/VibeRe.iss"
        if ($LASTEXITCODE) { throw 'Installer compilation failed.' }
    }
    Get-ChildItem -LiteralPath $dist -File | Sort-Object Name | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
    } | Set-Content "$dist/SHA256SUMS.txt" -Encoding ASCII
    Write-Host "Packages: $dist"
} finally { Pop-Location }
