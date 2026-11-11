param([Parameter(Mandatory)][string]$Installer,[Parameter(Mandatory)][string]$TestRoot)
$ErrorActionPreference='Stop'
$entry='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{843EDF4A-0EF8-409A-8B4D-BDB485E0D9DC}_is1'
$native='HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.synvaldi.bridge'
$run='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ((Test-Path $entry) -or (Test-Path $native) -or (Get-Item $run).GetValue('Synvaldi') -or (Get-Process Synvaldi -ErrorAction SilentlyContinue)) { throw 'Test requires a user without Synvaldi installed or running' }
$dest = Join-Path $TestRoot ("Установка Synvaldi "+[guid]::NewGuid().ToString('N'))
$exe = (Get-Item -LiteralPath $Installer).FullName
function Check([bool]$Condition,[string]$Description) {
    if (!$Condition) {throw $Description}
    Write-Output "PASS $Description"
}
function Install {
    $argsList=@('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/DIR="'+$dest+'"'))
    $setup=Start-Process -FilePath $exe -ArgumentList $argsList -PassThru -Wait
    Check ($setup.ExitCode -eq 0) 'installer exit 0'
}
try {
    Install
    Check ((Get-ItemProperty $entry).DisplayVersion -eq '0.1.0') 'Windows uninstall registration and version'
    $manifestPath=(Get-Item $native).GetValue('')
    $manifest=Get-Content -LiteralPath $manifestPath -Raw -Encoding utf8 | ConvertFrom-Json
    Check ((Test-Path -LiteralPath $manifest.path) -and $manifest.path.StartsWith($dest)) 'native host Unicode path'
    Check ($manifest.allowed_origins[0] -eq 'chrome-extension://jcfipkamaklpdghopicgfllhnigfjcio/') 'specific origin registered'
    Check ((Get-Item $run).GetValue('Synvaldi') -like '*--tray') 'Windows startup entry'
    Check (Test-Path (Join-Path $dest 'Synvaldi.Update.exe')) 'release updater installed'
    Install
    $process=Start-Process (Join-Path $dest 'Synvaldi.exe') -ArgumentList '--tray' -PassThru
    Start-Sleep -Seconds 3
    $process.Refresh()
    Check (!$process.HasExited -and $process.MainWindowHandle -eq 0) 'tray runs without foreground window'
    $before=$process.TotalProcessorTime
    Start-Sleep -Seconds 12
    $process.Refresh()
    [pscustomobject]@{IdleSampleSec=12;ProcessorMs=($process.TotalProcessorTime-$before).TotalMilliseconds;WorkingSetMiB=[math]::Round($process.WorkingSet64/1MB,1)} | ConvertTo-Json -Compress
    Start-Process (Join-Path $dest 'Synvaldi.exe') -ArgumentList '--quit' -Wait
    Check ($process.WaitForExit(12000)) 'tray exits cleanly'
} finally {
    $uninstaller=Join-Path $dest 'unins000.exe'
    if (Test-Path -LiteralPath $uninstaller) {
        $uninstall=Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT','/NORESTART','/SUPPRESSMSGBOXES') -PassThru -Wait
        Check ($uninstall.ExitCode -eq 0) 'system uninstall exit 0'
    }
}
Check (!(Test-Path $entry) -and !(Test-Path $native) -and !(Get-Item $run).GetValue('Synvaldi')) 'uninstall removes registration, native bridge and autostart'
Check (!(Test-Path (Join-Path $dest 'Synvaldi.exe'))) 'uninstall removes application binaries'

