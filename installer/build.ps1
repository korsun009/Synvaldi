param([string]$Compiler = (Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 6/ISCC.exe'), [switch]$SignManifest)
$ErrorActionPreference='Stop'
$root = Split-Path -Parent $PSScriptRoot
$projects = [ordered]@{
    desktop = 'desktop/Synvaldi.csproj'
    host = 'desktop/NativeHost/Synvaldi.NativeHost.csproj'
    updater = 'updater/Synvaldi.Update.csproj'
}
foreach ($part in $projects.Keys) {
    $destination = Join-Path $root "publish/$part"
    dotnet publish (Join-Path $root $projects[$part]) -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -o $destination
    if ($LASTEXITCODE) { throw "Publish failed: $part" }
}
# All three apphosts use the self-contained Windows desktop runtime located next to them.
# The installer copies helper assemblies and runtime configs without duplicating runtime DLLs.
& $Compiler (Join-Path $PSScriptRoot 'Synvaldi.iss')
if ($LASTEXITCODE) { throw 'Installer compilation failed' }
if ($SignManifest) { & (Join-Path $PSScriptRoot 'sign-release.ps1') -Version 0.1.0 -Installer (Join-Path $root 'dist/Synvaldi-0.1.0.exe') }
