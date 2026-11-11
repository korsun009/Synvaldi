param([Parameter(Mandatory)][string]$Installer,[Parameter(Mandatory)][version]$Version)
$ErrorActionPreference = 'Stop'
$file = Get-Item -LiteralPath $Installer
if ($file.Name -cne "Synvaldi-$Version.exe") { throw 'Installer name and version differ' }
$manifest = [ordered]@{Version="$Version";File=$file.Name;Length=$file.Length;Sha256=(Get-FileHash $file.FullName -Algorithm SHA256).Hash}
$bytes = [Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Compress))
$key = [Security.Cryptography.CngKey]::Open('Synvaldi Release Signing 2026')
$rsa = [Security.Cryptography.RSACng]::new($key)
try {
    $public = (Get-Content -LiteralPath "$PSScriptRoot/../updater/release-public.xml" -Raw).Trim()
    if ($rsa.ToXmlString($false) -cne $public) { throw 'Release key mismatch' }
    $sig = $rsa.SignData($bytes,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
    [IO.File]::WriteAllBytes((Join-Path $file.DirectoryName 'update.sig'),$sig)
    [IO.File]::WriteAllBytes((Join-Path $file.DirectoryName 'update.json'),$bytes)
} finally { $rsa.Dispose(); $key.Dispose() }
