# Install the compiler into ignored build tools, without changing the machine installation.
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Inno Setup requires Windows.' }
$tools = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/tools'
New-Item -ItemType Directory -Path $tools -Force | Out-Null
$download = Join-Path $tools 'innosetup-6.7.3.exe'
$compiler = Join-Path $tools 'inno/ISCC.exe'
if (!(Test-Path -LiteralPath $compiler)) {
    Invoke-WebRequest 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe' -OutFile $download -UseBasicParsing
    if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne '9C73C3BAE7ED48D44112A0F48E66742C00090BDB5BEF71D9D3C056C66E97B732') { throw 'Inno Setup checksum mismatch.' }
    $process = Start-Process -FilePath $download -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/PORTABLE=1', "/DIR=`"$tools/inno`"", '/NOICONS') -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode) { throw "Compiler installation failed: $($process.ExitCode)" }
}
Write-Output $compiler
